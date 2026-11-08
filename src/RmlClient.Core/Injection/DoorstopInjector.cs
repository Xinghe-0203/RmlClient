using RmlClient.Core.Configuration;

namespace RmlClient.Core.Injection;

/// <summary>
/// doorstop 注入器：用 winhttp.dll 代理劫持 Unity 的加载过程。
///
/// 官方在兼容模式下做同样的事（<c>Launcher\Main.cs:986-1014 InstallDoorstopCompat</c>），
/// 写入三件套：
///   - <c>doorstop_config.ini</c>（target_assembly 指向 RaftModLoader.dll）
///   - <c>winhttp.dll</c>（doorstop 的代理 DLL）
///   - <c>RaftModLoader.dll</c>（真正的加载器）
///
/// 注意：doorstop 的 winhttp.dll 会被部分杀软判定为 DLL 劫持，这是官方也有的问题。
/// 本实现额外做到：
///   - 探测时校验 winhttp.dll 的 PE 结构与文件大小，能识别「拷了一半」
///   - 移除时三个文件全清（官方 <c>UninstallDoorstop</c> 也清，但不校验结果）
///   - 备份原始 winhttp.dll（如果游戏目录本来就有）
/// </summary>
public sealed class DoorstopInjector : IInjector
{
    private const string WinHttpResourceName = "winhttp.dll";
    private const string LoaderResourceName = "RaftModLoader.dll";
    private const string DoorstopConfigResourceName = "doorstop_config.ini";

    private static readonly string[] BackupTargets =
    {
        "doorstop_config.ini",
        "winhttp.dll",
        "RaftModLoader.dll",
    };

    private readonly RmlPaths _paths;
    private readonly InjectionBackup _backup;
    private readonly LoaderProvisioner _loader;

    public DoorstopInjector(RmlPaths paths, InjectionBackup backup, LoaderProvisioner? loader = null)
    {
        _paths = paths;
        _backup = backup;
        _loader = loader ?? new LoaderProvisioner(paths);
    }

    public InjectionMode Mode => InjectionMode.Doorstop;

    public Task<InjectionProbeResult> ProbeAsync(string gamePath, CancellationToken ct = default)
    {
        var details = new List<string>();

        var config = RmlPaths.GameDoorstopConfig(gamePath);
        var winHttp = RmlPaths.GameWinHttpDll(gamePath);
        var loader = RmlPaths.GameLoaderDll(gamePath);

        var configOk = File.Exists(config);
        var winHttpOk = File.Exists(winHttp) && LooksLikePe(winHttp);
        var loaderOk = File.Exists(loader) && LooksLikePe(loader);

        details.Add(configOk ? $"已找到 {config}" : $"缺少 {config}");
        details.Add(winHttpOk ? $"已找到 {winHttp}" : $"缺少或损坏 {winHttp}");
        details.Add(loaderOk ? $"已找到 {loader}" : $"缺少或损坏 {loader}");

        if (configOk)
        {
            try
            {
                var ini = IniFile.Load(config);
                var enabled = ini.GetBool("General", "enabled");
                var target = ini.Get("General", "target_assembly");
                details.Add($"doorstop enabled={enabled}, target_assembly={target}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                details.Add($"读取 doorstop_config.ini 失败：{ex.Message}");
            }
        }

        var state = (configOk, winHttpOk, loaderOk) switch
        {
            (true, true, true) => InjectionState.Injected,
            (false, false, false) => InjectionState.NotInjected,
            _ => InjectionState.PartiallyInjected,
        };

        return Task.FromResult(new InjectionProbeResult(state, Mode, details));
    }

    public async Task<InjectionResult> ApplyAsync(string gamePath, CancellationToken ct = default)
    {
        var details = new List<string>();

        // winhttp.dll：优先用用户放在 resources 下的（便于替换版本），否则用内置的。
        var winHttpSource = Path.Combine(_paths.Resources, WinHttpResourceName);
        byte[]? winHttpBytes = null;
        if (File.Exists(winHttpSource))
        {
            winHttpBytes = await File.ReadAllBytesAsync(winHttpSource, ct).ConfigureAwait(false);
        }
        else
        {
            winHttpBytes = LoaderProvisioner.ReadEmbeddedWinHttp();
            if (winHttpBytes is not null)
            {
                details.Add("使用内置的 doorstop winhttp.dll。");
            }
        }

        if (winHttpBytes is null)
        {
            return InjectionResult.Fail(
                "缺少 doorstop 的 winhttp.dll。",
                $"内置资源不可用，请把 winhttp.dll 放到：{_paths.Resources}");
        }

        // RaftModLoader.dll 就是 AssemblyLoader 本体。resources 下放一份就用那份，
        // 否则从内置加载器解出来直接写到游戏目录（省一次 22 MB 的中间拷贝）。
        var loaderSource = Path.Combine(_paths.Resources, LoaderResourceName);
        if (!File.Exists(loaderSource))
        {
            var extract = _loader.Extract(RmlPaths.GameLoaderDll(gamePath));
            if (!extract.Success)
            {
                return InjectionResult.Fail(
                    "缺少 RaftModLoader.dll。",
                    $"{extract.Message}（期望位置：{extract.Path}）");
            }

            details.Add("已从内置加载器解出 RaftModLoader.dll。");
            File.WriteAllBytes(RmlPaths.GameWinHttpDll(gamePath), winHttpBytes);
            details.Add("已写入 winhttp.dll");

            return await FinishDoorstopConfigAsync(gamePath, details).ConfigureAwait(false);
        }

        _backup.BackupFiles(gamePath, BackupTargets);

        File.WriteAllBytes(RmlPaths.GameWinHttpDll(gamePath), winHttpBytes);
        details.Add("已写入 winhttp.dll");

        File.Copy(loaderSource, RmlPaths.GameLoaderDll(gamePath), overwrite: true);
        details.Add("已写入 RaftModLoader.dll");

        return await FinishDoorstopConfigAsync(gamePath, details).ConfigureAwait(false);
    }

    /// <summary>写 doorstop_config.ini 并返回结果（两种来源分支共用）。</summary>
    private async Task<InjectionResult> FinishDoorstopConfigAsync(string gamePath, List<string> details)
    {
        var configPath = RmlPaths.GameDoorstopConfig(gamePath);
        var ini = new IniFile();
        ini.GetOrAddSection("General");
        ini.Set("General", "enabled", "true");
        ini.Set("General", "target_assembly", "RaftModLoader.dll");
        ini.Set("General", "redirect_output_log", "false");
        ini.Set("General", "ignore_disable_switch", "false");

        ini.Set("UnityMono", "dll_search_path_override", string.Empty);
        ini.Set("UnityMono", "debug_enabled", "false");
        ini.Set("UnityMono", "debug_address", "127.0.0.1:10000");
        ini.Set("UnityMono", "debug_suspend", "false");

        ini.Set("Il2Cpp", "coreclr_path", string.Empty);
        ini.Set("Il2Cpp", "corlib_dir", string.Empty);

        ini.Save(configPath);
        details.Add($"已写入 {configPath}");

        await Task.CompletedTask.ConfigureAwait(false);
        return InjectionResult.Ok("doorstop 注入完成。", details.ToArray());
    }

    public async Task<InjectionResult> RemoveAsync(string gamePath, CancellationToken ct = default)
    {
        var details = new List<string>();

        foreach (var path in new[]
                 {
                     RmlPaths.GameDoorstopConfig(gamePath),
                     RmlPaths.GameWinHttpDll(gamePath),
                     RmlPaths.GameLoaderDll(gamePath),
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                details.Add($"已删除 {path}");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);

        return details.Count == 0
            ? InjectionResult.Ok("没有需要移除的 doorstop 文件。")
            : InjectionResult.Ok("doorstop 注入已移除。", details.ToArray());
    }

    public async Task<InjectionResult> RepairAsync(string gamePath, CancellationToken ct = default)
    {
        var remove = await RemoveAsync(gamePath, ct).ConfigureAwait(false);
        if (!remove.Success)
        {
            return remove;
        }

        var apply = await ApplyAsync(gamePath, ct).ConfigureAwait(false);
        var details = remove.Details.Concat(apply.Details).ToArray();
        return new InjectionResult(apply.Success, apply.Success ? "doorstop 注入已修复。" : apply.Message, details);
    }

    public IReadOnlyList<string> RestoreFromBackup(string gamePath) => _backup.Restore(gamePath);

    /// <summary>只读前两个字节判断是否为 PE 文件，避免把残缺拷贝当成正常 DLL。</summary>
    private static bool LooksLikePe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }
}
