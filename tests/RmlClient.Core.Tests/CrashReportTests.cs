using RmlClient.Core.Diagnostics;
using RmlClient.Core.Logging;

namespace RmlClient.Core.Tests;

/// <summary>
/// 崩溃报告的两个关键行为：
///   1. 脱敏必须真的把用户名 / 本机路径换掉（这是承诺给用户的隐私保证）
///   2. 报告不能无限膨胀（上传服务有大小限制）
/// </summary>
public class CrashReportTests
{
    private static LogEntry Entry(LogLevel level, string source, string message) =>
        new(DateTimeOffset.Now, level, source, message);

    [Fact]
    public void Redact_ReplacesUserProfilePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(profile));

        var text = $"读取 {Path.Combine(profile, "AppData", "RaftModLoader", "config.ini")} 失败";

        var redacted = CrashReportBuilder.Redact(text);

        Assert.Contains("%USERPROFILE%", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(profile, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_ReplacesForwardSlashVariantOfProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var forward = profile.Replace('\\', '/');

        var redacted = CrashReportBuilder.Redact($"path={forward}/AppData/x");

        Assert.Contains("%USERPROFILE%", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(forward, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_ReplacesUserName()
    {
        var user = Environment.UserName;
        Assert.False(string.IsNullOrWhiteSpace(user));

        var redacted = CrashReportBuilder.Redact($"登录用户 {user} 启动");

        Assert.Contains("%USER%", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(user, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_ReplacesMachineName()
    {
        var machine = Environment.MachineName;
        var user = Environment.UserName;
        Assert.False(string.IsNullOrWhiteSpace(machine));

        var redacted = CrashReportBuilder.Redact($"host={machine}");

        // 用户名与机器名相同是常见情况（用自己名字命名电脑），
        // 这时统一替换成 %USER% 就够了，不必再区分。
        var expected = string.Equals(user, machine, StringComparison.OrdinalIgnoreCase)
            ? "%USER%"
            : "%MACHINE%";

        Assert.Contains(expected, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(machine, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_KeepsUnrelatedPaths()
    {
        // 游戏目录不在用户目录下（可能在别的盘），必须原样保留 ——
        // 过度替换会把定位问题所需的信息吃掉。
        const string text = @"gamePath = D:\SteamLibrary\steamapps\common\Raft";

        Assert.Equal(text, CrashReportBuilder.Redact(text));
    }

    [Fact]
    public void Redact_HandlesEmptyInput()
    {
        Assert.Equal(string.Empty, CrashReportBuilder.Redact(string.Empty));
    }

    [Fact]
    public void Build_ContainsHeaderExceptionAndLogs()
    {
        var entries = new[]
        {
            Entry(LogLevel.Info, "App", "客户端启动"),
            Entry(LogLevel.Error, "Crash", "UI 线程未处理异常"),
        };

        var report = CrashReportBuilder.Build(
            entries,
            new InvalidOperationException("boom"),
            "UI 线程未处理异常");

        Assert.Contains("RML Client 崩溃报告", report, StringComparison.Ordinal);
        Assert.Contains("客户端版本", report, StringComparison.Ordinal);
        Assert.Contains("场景：UI 线程未处理异常", report, StringComparison.Ordinal);
        Assert.Contains("----- 异常 -----", report, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("boom", report, StringComparison.Ordinal);
        Assert.Contains("客户端启动", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutException_OmitsExceptionSection()
    {
        var report = CrashReportBuilder.Build(new[] { Entry(LogLevel.Info, "App", "x") });

        Assert.DoesNotContain("----- 异常 -----", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KeepsOnlyLastLogLines()
    {
        var entries = Enumerable.Range(0, 500)
            .Select(i => Entry(LogLevel.Info, "App", $"line-{i:D4}"))
            .ToArray();

        var report = CrashReportBuilder.Build(entries);

        // 只保留最后 300 条：最早的 200 条必须被丢掉，最近的必须还在。
        Assert.DoesNotContain("line-0000", report, StringComparison.Ordinal);
        Assert.DoesNotContain("line-0199", report, StringComparison.Ordinal);
        Assert.Contains("line-0200", report, StringComparison.Ordinal);
        Assert.Contains("line-0499", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TruncatesOversizedReport()
    {
        var entries = Enumerable.Range(0, 300)
            .Select(i => Entry(LogLevel.Info, "App", new string('x', 1000)))
            .ToArray();

        var report = CrashReportBuilder.Build(entries);

        Assert.True(report.Length <= 60_000 + 40, $"报告长度 {report.Length} 超出上限");
        Assert.Contains("报告已截断", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RedactsUserProfileInLogs()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var report = CrashReportBuilder.Build(new[]
        {
            Entry(LogLevel.Error, "Loader", $"找不到 {profile}\\RaftModLoader\\binaries\\coremod.dll"),
        });

        Assert.DoesNotContain(profile, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", report, StringComparison.Ordinal);
    }
}
