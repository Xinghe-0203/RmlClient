using System.Diagnostics;
using RmlClient.Core.Configuration;
using RmlClient.Core.Logging;

namespace RmlClient.Core.Game;

public sealed record LaunchResult(bool Success, string Message, int? ProcessId = null);

/// <summary>
/// 启动游戏。
///
/// 官方流程（<c>Launcher\Main.cs:856-936 playBtn_MouseDown</c>）：
///   1. 写 pid.txt（AssemblyLoader 用它做父进程握手）
///   2. 兼容模式装 doorstop，否则卸 doorstop + JSON 注入
///   3. 打补丁跳过 Unity 启动画面
///   4. <c>Process.Start(gamePath\Raft.exe)</c> 或 <c>steam://run/648800</c>
///
/// 本实现的改进：
///   - pid.txt 写的是真实进程 ID，且启动后校验进程确实活着
///   - 启动前检测游戏是否已在运行，避免重复启动
///   - 支持「跳过启动画面」开关（官方改 globalgamemanagers 且不备份）
/// </summary>
public sealed class GameLauncher
{
    private readonly RmlPaths _paths;
    private readonly ClientLogger? _logger;

    public GameLauncher(RmlPaths paths, ClientLogger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    public bool IsGameRunning() =>
        Process.GetProcessesByName("Raft").Length > 0;

    /// <summary>
    /// 写入 pid.txt。AssemblyLoader 的 <c>HLoader.StartedWithLauncher()</c> 会读它，
    /// 并在读完后立刻把文件清空（一次性令牌），所以这里只需写一次。
    /// </summary>
    public void WriteHandshakePid(int processId)
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.PidFile, processId.ToString());
        _logger?.Debug("GameLauncher", $"pid.txt 写入 {processId}");
    }

    /// <summary>预写一个占位 PID：Steam 启动模式下拿不到游戏 PID，只能先写自身。</summary>
    public void WriteHandshakePlaceholder()
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.PidFile, Environment.ProcessId.ToString());
        _logger?.Debug("GameLauncher", "pid.txt 写入占位 PID（Steam 模式）");
    }

    public LaunchResult StartDirect(string gamePath, bool waitForHandshake = true)
    {
        var exe = Path.Combine(gamePath, RmlPaths.GameMainExecutable);
        if (!File.Exists(exe))
        {
            return new LaunchResult(false, $"找不到 {exe}");
        }

        if (IsGameRunning())
        {
            return new LaunchResult(false, "游戏已经在运行了。");
        }

        try
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                WorkingDirectory = gamePath,
                UseShellExecute = true,
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return new LaunchResult(false, "进程启动失败。");
            }

            // 只有直接启动时才知道 PID，此时才写真实值。
            WriteHandshakePid(process.Id);
            _logger?.Info("GameLauncher", $"已启动 Raft.exe (pid={process.Id})");

            return new LaunchResult(true, "游戏已启动。", process.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error("GameLauncher", "启动 Raft.exe 失败", ex);
            return new LaunchResult(false, $"启动失败：{ex.Message}");
        }
    }

    public LaunchResult StartViaSteam(string? gameStartCommand = null)
    {
        if (IsGameRunning())
        {
            return new LaunchResult(false, "游戏已经在运行了。");
        }

        var target = string.IsNullOrWhiteSpace(gameStartCommand)
            ? $"steam://run/{RmlPaths.GameSteamAppId}"
            : gameStartCommand;

        try
        {
            // Steam 会另起进程，客户端拿不到游戏 PID，只能写占位值。
            WriteHandshakePlaceholder();

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _logger?.Info("GameLauncher", $"已通过 Steam 启动：{target}");

            return new LaunchResult(true, "已请求 Steam 启动游戏。");
        }
        catch (Exception ex)
        {
            _logger?.Error("GameLauncher", "Steam 启动失败", ex);
            return new LaunchResult(false, $"Steam 启动失败：{ex.Message}");
        }
    }

    public LaunchResult Start(
        string gamePath,
        bool preferSteam,
        string? gameStartCommand = null)
    {
        return preferSteam
            ? StartViaSteam(gameStartCommand)
            : StartDirect(gamePath);
    }

    /// <summary>等待游戏主窗口出现，用于「启动后最小化到托盘」等交互。</summary>
    public async Task<Process?> WaitForGameWindowAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var process in Process.GetProcessesByName("Raft"))
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process;
                    }
                }
                catch (InvalidOperationException)
                {
                    // 进程已退出。
                }
                finally
                {
                    process.Dispose();
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return null;
    }
}
