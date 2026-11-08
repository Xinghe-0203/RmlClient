using RmlClient.Core.Configuration;
using RmlClient.Core.Injection;
using RmlClient.Core.Logging;

namespace RmlClient.Core.Diagnostics;

/// <summary>
/// 把环境还原成「官方启动器接管」的状态。
///
/// 设计文档 §9 风险表要求：用户可能同时装了官方 HMLCore 与本客户端，
/// 混用会导致注入状态错乱，因此必须提供一键「还原为官方」。
///
/// 还原动作只碰「本客户端引入的东西」与「注入备份里记录的游戏文件」：
///   - 移除两种注入模式的全部产物（含从备份还原游戏原始 JSON）
///   - 用 GGM 备份还原 <c>globalgamemanagers</c>
///   - 删除本客户端释放的 <c>binaries\HMLCoreLibrary.dll</c>（官方会自己重新释放）
///   - 保留 mods、logs、config.ini，绝不删用户的模组
/// </summary>
public sealed class OfficialRestore
{
    private readonly RmlPaths _paths;
    private readonly InjectionBackup _backup;
    private readonly JsonInjector _json;
    private readonly DoorstopInjector _doorstop;
    private readonly ClientLogger? _logger;

    public OfficialRestore(
        RmlPaths paths,
        InjectionBackup backup,
        JsonInjector json,
        DoorstopInjector doorstop,
        ClientLogger? logger = null)
    {
        _paths = paths;
        _backup = backup;
        _json = json;
        _doorstop = doorstop;
        _logger = logger;
    }

    public sealed record RestoreStep(string Title, bool Success, string Detail);

    public sealed record RestoreReport(IReadOnlyList<RestoreStep> Steps, bool Success)
    {
        public string Summary => string.Join(
            Environment.NewLine,
            Steps.Select(s => $"{(s.Success ? "✓" : "✗")} {s.Title}：{s.Detail}"));
    }

    /// <summary>
    /// 执行还原。
    /// </summary>
    /// <param name="gamePath">游戏根目录，可为空（为空时跳过游戏侧还原）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<RestoreReport> RestoreAsync(string? gamePath, CancellationToken ct = default)
    {
        var steps = new List<RestoreStep>();
        var hasGame = !string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath);

        if (hasGame)
        {
            // 两种模式都跑一遍：用户可能先试过 JSON 又切到 doorstop，
            // 只移除当前配置的模式会留下另一种模式的残留。
            steps.Add(await RemoveInjectionAsync(_json, gamePath!, ct));
            steps.Add(await RemoveInjectionAsync(_doorstop, gamePath!, ct));

            steps.Add(RestoreGameManagers(gamePath!));
        }
        else
        {
            steps.Add(new RestoreStep("游戏目录", false, "未配置有效的游戏路径，跳过游戏侧还原。"));
        }

        steps.Add(RestoreLoaderDll());

        var success = steps.All(s => s.Success);
        _logger?.Info("Restore", success
            ? "已还原为官方启动器可接管的状态。"
            : "还原完成，但有步骤未成功，详见报告。");

        return new RestoreReport(steps, success);
    }

    private async Task<RestoreStep> RemoveInjectionAsync(
        IInjector injector,
        string gamePath,
        CancellationToken ct)
    {
        var name = injector.Mode == InjectionMode.Doorstop ? "doorstop 注入" : "JSON 注入";

        try
        {
            var probe = await injector.ProbeAsync(gamePath, ct);
            if (probe.State == InjectionState.NotInjected)
            {
                return new RestoreStep(name, true, "本来就没有注入。");
            }

            var result = await injector.RemoveAsync(gamePath, ct);
            return new RestoreStep(name, result.Success, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RestoreStep(name, false, $"移除失败：{ex.Message}");
        }
    }

    private RestoreStep RestoreGameManagers(string gamePath)
    {
        const string Title = "globalgamemanagers";
        var backupPath = Path.Combine(_paths.CacheBackup, "ggm", "globalgamemanagers.original");
        var target = RmlPaths.GameGlobalGameManagers(gamePath);

        if (!File.Exists(backupPath))
        {
            return new RestoreStep(Title, true, "没有改写记录，无需还原。");
        }

        try
        {
            File.Copy(backupPath, target, overwrite: true);
            return new RestoreStep(Title, true, "已用备份还原为原始文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreStep(Title, false, $"还原失败：{ex.Message}");
        }
    }

    private RestoreStep RestoreLoaderDll()
    {
        const string Title = "binaries\\HMLCoreLibrary.dll";

        try
        {
            if (!File.Exists(_paths.AssemblyLoaderDll))
            {
                return new RestoreStep(Title, true, "文件不存在，无需删除。");
            }

            File.Delete(_paths.AssemblyLoaderDll);
            return new RestoreStep(Title, true, "已删除（官方启动器下次运行会重新释放自己的版本）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RestoreStep(Title, false, $"删除失败：{ex.Message}");
        }
    }

    /// <summary>是否还有可还原的注入备份（供 UI 决定按钮是否可用）。</summary>
    public bool HasInjectionBackup => _backup.HasBackup;

    /// <summary>是否有 GGM 备份。</summary>
    public bool HasGameManagersBackup =>
        File.Exists(Path.Combine(_paths.CacheBackup, "ggm", "globalgamemanagers.original"));
}
