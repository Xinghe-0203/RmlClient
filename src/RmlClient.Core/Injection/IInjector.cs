using System.Text.Json;
using System.Text.Json.Nodes;
using RmlClient.Core.Configuration;

namespace RmlClient.Core.Injection;

public enum InjectionState
{
    /// <summary>完全未注入。</summary>
    NotInjected = 0,

    /// <summary>注入完整且自洽。</summary>
    Injected = 1,

    /// <summary>注入了一半：例如 DLL 拷了但 JSON 没改，或 JSON 改了但 DLL 缺失。</summary>
    PartiallyInjected = 2,

    /// <summary>注入痕迹存在但内容损坏：JSON 解析失败、DLL 大小不符。</summary>
    Corrupted = 3,
}

public sealed record InjectionProbeResult(
    InjectionState State,
    InjectionMode Mode,
    IReadOnlyList<string> Details)
{
    public bool IsHealthy => State == InjectionState.Injected;
}

public sealed record InjectionResult(bool Success, string Message, IReadOnlyList<string> Details)
{
    public static InjectionResult Ok(string message, params string[] details) =>
        new(true, message, details);

    public static InjectionResult Fail(string message, params string[] details) =>
        new(false, message, details);
}

/// <summary>
/// 注入器接口。两种模式都必须支持探测、应用、移除、修复。
///
/// 官方实现只有「写」没有「探测」与「还原」，导致半损坏状态无法诊断，
/// 卸载也不清注入产物（见 <c>Launcher\Main.cs:943-968</c> 只删 doorstop 三件套）。
/// </summary>
public interface IInjector
{
    InjectionMode Mode { get; }

    Task<InjectionProbeResult> ProbeAsync(string gamePath, CancellationToken ct = default);

    Task<InjectionResult> ApplyAsync(string gamePath, CancellationToken ct = default);

    Task<InjectionResult> RemoveAsync(string gamePath, CancellationToken ct = default);

    Task<InjectionResult> RepairAsync(string gamePath, CancellationToken ct = default);
}

/// <summary>注入前备份原始游戏文件，可一键还原。官方完全不做这件事。</summary>
public sealed class InjectionBackup
{
    private const string ManifestName = "injection-manifest.json";

    private readonly RmlPaths _paths;

    public InjectionBackup(RmlPaths paths) => _paths = paths;

    private string BackupRoot => Path.Combine(_paths.CacheBackup, "injection");

    private string ManifestPath => Path.Combine(BackupRoot, ManifestName);

    public sealed record BackupEntry(string RelativePath, bool ExistedBefore, string? BackupFile);

    private sealed record Manifest(string GamePath, DateTimeOffset CreatedAt, List<BackupEntry> Entries);

    /// <summary>备份指定文件（相对游戏根目录）。已备份过的不覆盖，保证首次状态可还原。</summary>
    public void BackupFiles(string gamePath, IEnumerable<string> relativePaths)
    {
        Directory.CreateDirectory(BackupRoot);

        var manifest = ReadManifest() ?? new Manifest(gamePath, DateTimeOffset.UtcNow, new List<BackupEntry>());
        var known = manifest.Entries.Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var relative in relativePaths)
        {
            if (known.Contains(relative))
            {
                continue;
            }

            var source = Path.Combine(gamePath, relative);
            if (File.Exists(source))
            {
                var safeName = relative.Replace('\\', '_').Replace('/', '_');
                var target = Path.Combine(BackupRoot, safeName);
                File.Copy(source, target, overwrite: true);
                manifest.Entries.Add(new BackupEntry(relative, true, safeName));
            }
            else
            {
                manifest.Entries.Add(new BackupEntry(relative, false, null));
            }

            known.Add(relative);
            changed = true;
        }

        if (changed)
        {
            WriteManifest(manifest with { GamePath = gamePath });
        }
    }

    public bool HasBackup => File.Exists(ManifestPath);

    /// <summary>把备份的文件全部还原；原本不存在的文件删除。</summary>
    public IReadOnlyList<string> Restore(string gamePath)
    {
        var manifest = ReadManifest();
        var restored = new List<string>();
        if (manifest is null)
        {
            return restored;
        }

        foreach (var entry in manifest.Entries)
        {
            var target = Path.Combine(gamePath, entry.RelativePath);

            if (entry.ExistedBefore && entry.BackupFile is not null)
            {
                var source = Path.Combine(BackupRoot, entry.BackupFile);
                if (File.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                    restored.Add(entry.RelativePath);
                }
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
                restored.Add(entry.RelativePath + " (deleted)");
            }
        }

        return restored;
    }

    public void ClearBackup()
    {
        if (Directory.Exists(BackupRoot))
        {
            Directory.Delete(BackupRoot, recursive: true);
        }
    }

    private Manifest? ReadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteManifest(Manifest manifest) =>
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
}
