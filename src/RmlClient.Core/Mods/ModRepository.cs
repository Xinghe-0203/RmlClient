using System.Text.Json;
using RmlClient.Core.Configuration;

namespace RmlClient.Core.Mods;

/// <summary>
/// mod 目录扫描与管理。
///
/// 启用/禁用机制与官方一致：coremod 只加载 <c>*.rmod</c>（
/// <c>ModManagerPage.cs:121,581</c> 的 <c>x.Name.ToLower().EndsWith(".rmod")</c>），
/// 所以禁用 = 重命名为 <c>*.rmod.disabled</c>。
/// </summary>
public sealed class ModRepository
{
    public const string EnabledExtension = RmlPaths.ModExtension;
    public const string DisabledExtension = RmlPaths.DisabledModExtension;

    /// <summary>文件夹形式 mod 的快捷方式（官方 FolderModHandler 那条路径）。</summary>
    public const string ShortcutExtension = ".lnk";
    public const string DisabledShortcutExtension = ".lnk.disabled";

    private readonly string _modsFolder;

    public ModRepository(string modsFolder) => _modsFolder = modsFolder;

    public string ModsFolder => _modsFolder;

    public bool Exists => Directory.Exists(_modsFolder);

    /// <summary>
    /// 扫描 mod 目录。官方只扫顶层（SearchOption.TopDirectoryOnly），这里保持一致。
    /// </summary>
    public IReadOnlyList<ModEntry> Scan()
    {
        if (!Directory.Exists(_modsFolder))
        {
            return Array.Empty<ModEntry>();
        }

        var result = new List<ModEntry>();

        foreach (var file in Directory.EnumerateFiles(_modsFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var isEnabled = name.EndsWith(EnabledExtension, StringComparison.OrdinalIgnoreCase);
            var isDisabled = name.EndsWith(DisabledExtension, StringComparison.OrdinalIgnoreCase);

            // .lnk 快捷方式 mod：官方 ModManagerPage.cs:121 也认它，
            // 走 FolderModHandler 读目标目录里的 modinfo.json。
            var isShortcut = name.EndsWith(ShortcutExtension, StringComparison.OrdinalIgnoreCase);
            var isShortcutDisabled = name.EndsWith(DisabledShortcutExtension, StringComparison.OrdinalIgnoreCase);

            if (!isEnabled && !isDisabled && !isShortcut && !isShortcutDisabled)
            {
                continue;
            }

            if (isShortcut || isShortcutDisabled)
            {
                result.Add(ReadShortcutEntry(file, name, isShortcut));
                continue;
            }

            var info = default(ModInfo);
            string? error = null;

            try
            {
                info = RmodReader.ReadModInfo(file);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var fileInfo = new FileInfo(file);
            result.Add(new ModEntry
            {
                FilePath = file,
                FileName = name,
                IsEnabled = isEnabled,
                FileSize = fileInfo.Length,
                LastWriteTime = fileInfo.LastWriteTimeUtc,
                Info = info,
                ParseError = error,
            });
        }

        return result
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>读一个 .lnk mod 条目：解析目标目录 + 目标目录里的 modinfo.json。</summary>
    private static ModEntry ReadShortcutEntry(string file, string name, bool isEnabled)
    {
        var target = ShortcutResolver.ResolveTarget(file);

        var info = default(ModInfo);
        string? error = null;
        long size = 0;
        var lastWrite = DateTimeOffset.UtcNow;

        if (string.IsNullOrEmpty(target))
        {
            error = "无法解析快捷方式目标。";
        }
        else if (!Directory.Exists(target))
        {
            error = $"快捷方式指向的目录不存在：{target}";
        }
        else
        {
            try
            {
                info = RmodReader.ReadFolderModInfo(target);
                var dirInfo = new DirectoryInfo(target);
                lastWrite = dirInfo.LastWriteTimeUtc;

                // 目录形式的 mod 没有单一文件大小，用 modinfo.json 的大小占位，
                // 只用于 UI 上区分「这是个目录 mod」。
                var manifest = new FileInfo(Path.Combine(target, "modinfo.json"));
                size = manifest.Exists ? manifest.Length : 0;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        return new ModEntry
        {
            FilePath = file,
            FileName = name,
            IsEnabled = isEnabled,
            FileSize = size,
            LastWriteTime = lastWrite,
            Info = info,
            ParseError = error,
            ShortcutTarget = target,
        };
    }

    public ModOperationResult SetEnabled(ModEntry entry, bool enabled)
    {
        if (!File.Exists(entry.FilePath))
        {
            return ModOperationResult.Fail($"文件不存在：{entry.FilePath}");
        }

        var directory = Path.GetDirectoryName(entry.FilePath)!;
        var baseName = entry.FileName;

        // 禁用态后缀有两种：.rmod.disabled（压缩包）与 .lnk.disabled（文件夹 mod）。
        var disabledSuffix = baseName.EndsWith(DisabledShortcutExtension, StringComparison.OrdinalIgnoreCase)
            ? DisabledShortcutExtension
            : baseName.EndsWith(DisabledExtension, StringComparison.OrdinalIgnoreCase)
                ? DisabledExtension
                : null;

        if (enabled)
        {
            if (disabledSuffix is null)
            {
                return ModOperationResult.Ok("已经是启用状态。");
            }

            // 去掉 ".disabled" 尾巴就是原来的启用态文件名。
            var enabledName = baseName[..^".disabled".Length];
            var target = Path.Combine(directory, enabledName);

            return Move(entry.FilePath, target, "已启用。");
        }

        if (disabledSuffix is not null)
        {
            return ModOperationResult.Ok("已经是禁用状态。");
        }

        var disabledTarget = Path.Combine(directory, baseName + ".disabled");
        return Move(entry.FilePath, disabledTarget, "已禁用。");
    }

    /// <summary>
    /// 安装一个 .rmod 文件：先校验 ZIP 结构与 modinfo.json，再原子移入。
    /// 不做路径拼接、不解压，避免 Zip-Slip 与包内文件名污染。
    /// </summary>
    /// <param name="sourceRmodPath">待安装的 .rmod 源文件。</param>
    /// <param name="targetFileName">
    /// 落盘文件名。传 null 时沿用源文件名；市场安装传官方命名
    /// <c>modinstaller.&lt;slug&gt;.rmod</c>，与官方启动器保持一致，
    /// 这样两边的「已安装」判断和 coremod 的扫描逻辑都能对得上。
    /// </param>
    public ModOperationResult Install(string sourceRmodPath, string? targetFileName = null)
    {
        if (!File.Exists(sourceRmodPath))
        {
            return ModOperationResult.Fail($"源文件不存在：{sourceRmodPath}");
        }

        ModInfo info;
        try
        {
            info = RmodReader.ReadModInfo(sourceRmodPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
        {
            return ModOperationResult.Fail($"不是有效的 .rmod 包：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(info.Name))
        {
            return ModOperationResult.Fail("modinfo.json 缺少 name 字段。");
        }

        Directory.CreateDirectory(_modsFolder);

        var requestedName = string.IsNullOrWhiteSpace(targetFileName)
            ? Path.GetFileName(sourceRmodPath)
            : targetFileName;

        // 只允许落在 mods 目录里：文件名不能带路径分隔符。
        // 必须先校验再 GetFileName —— 否则 "..\escape.rmod" 会被静默改写成
        // "escape.rmod"，调用方以为装到了别处，实际名字已经变了。
        if (requestedName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
        {
            return ModOperationResult.Fail($"非法的目标文件名：{requestedName}");
        }

        var fileName = Path.GetFileName(requestedName);

        var target = Path.Combine(_modsFolder, fileName);

        // 同名已存在时先挪到备份名，安装失败可回滚。
        string? displaced = null;
        if (File.Exists(target))
        {
            displaced = target + ".old";
            File.Move(target, displaced, overwrite: true);
        }

        try
        {
            File.Copy(sourceRmodPath, target, overwrite: true);

            // 拷完再验一次，防止拷贝过程中源文件被改。
            _ = RmodReader.ReadModInfo(target);

            if (displaced is not null)
            {
                File.Delete(displaced);
            }

            return ModOperationResult.Ok($"已安装 {info.Name} {info.Version}。");
        }
        catch (Exception ex)
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            if (displaced is not null && File.Exists(displaced))
            {
                File.Move(displaced, target, overwrite: true);
            }

            return ModOperationResult.Fail($"安装失败：{ex.Message}");
        }
    }

    /// <summary>卸载到回收站而不是直接删除。</summary>
    public ModOperationResult Uninstall(ModEntry entry)
    {
        if (!File.Exists(entry.FilePath))
        {
            return ModOperationResult.Fail($"文件不存在：{entry.FilePath}");
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                entry.FilePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            return ModOperationResult.Ok($"已移到回收站：{entry.DisplayName}");
        }
        catch (Exception ex)
        {
            return ModOperationResult.Fail($"卸载失败：{ex.Message}");
        }
    }

    private static ModOperationResult Move(string source, string target, string successMessage)
    {
        try
        {
            if (File.Exists(target))
            {
                return ModOperationResult.Fail($"目标文件已存在：{target}");
            }

            File.Move(source, target);
            return ModOperationResult.Ok(successMessage);
        }
        catch (Exception ex)
        {
            return ModOperationResult.Fail($"重命名失败：{ex.Message}");
        }
    }
}

public sealed record ModOperationResult(bool Success, string Message)
{
    public static ModOperationResult Ok(string message) => new(true, message);

    public static ModOperationResult Fail(string message) => new(false, message);
}
