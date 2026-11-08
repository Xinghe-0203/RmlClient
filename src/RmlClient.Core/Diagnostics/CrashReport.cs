using System.Net.Http.Headers;
using System.Text;
using RmlClient.Core.Configuration;
using RmlClient.Core.Logging;

namespace RmlClient.Core.Diagnostics;

/// <summary>
/// 生成崩溃报告并（可选）上传到公开 paste 服务。
///
/// 设计文档 §7 第 10 条的要求：
///   - 默认关闭（<c>config.Client.UploadCrashLogs</c>，默认 false）
///   - 必须显式同意
///   - 上传前可预览内容
///
/// 报告里会带本地路径与用户名，这是它有价值的原因（能定位到具体环境），
/// 也是它的风险。所以这里额外做一层脱敏：把用户目录替换成 <c>%USERPROFILE%</c>、
/// 把用户名替换成 <c>%USER%</c>。用户看到的预览就是最终上传的内容，不做二次加工。
/// </summary>
public static class CrashReportBuilder
{
    /// <summary>报告最多带多少条日志，避免上传几十 MB。</summary>
    private const int MaxLogLines = 300;

    /// <summary>报告正文上限（字符）。dpaste 有大小限制，这里留足余量。</summary>
    private const int MaxReportChars = 60_000;

    /// <summary>
    /// 组装报告正文。
    /// </summary>
    /// <param name="entries">最近的日志条目（按时间正序）。</param>
    /// <param name="exception">触发报告的异常，可为 null（用户手动反馈时）。</param>
    /// <param name="context">额外上下文，例如「启动游戏时」「安装 mod 时」。</param>
    public static string Build(
        IReadOnlyList<LogEntry> entries,
        Exception? exception = null,
        string? context = null)
    {
        var builder = new StringBuilder();

        builder.AppendLine("===== RML Client 崩溃报告 =====");
        builder.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"客户端版本：{Updates.ClientVersion.Current}");
        builder.AppendLine($"操作系统：{Environment.OSVersion.VersionString}");
        builder.AppendLine($"运行时：.NET {Environment.Version}");
        builder.AppendLine($"64 位进程：{Environment.Is64BitProcess}");

        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.AppendLine($"场景：{context}");
        }

        if (exception is not null)
        {
            builder.AppendLine();
            builder.AppendLine("----- 异常 -----");
            builder.AppendLine(exception.ToString());
        }

        builder.AppendLine();
        builder.AppendLine($"----- 最近日志（最多 {MaxLogLines} 条）-----");

        foreach (var entry in entries.TakeLast(MaxLogLines))
        {
            builder.AppendLine(entry.Format());
        }

        var text = Redact(builder.ToString());

        return text.Length <= MaxReportChars
            ? text
            : text[..MaxReportChars] + Environment.NewLine + "…（报告已截断）";
    }

    /// <summary>
    /// 脱敏：把本机用户目录、用户名、常见隐私路径换成占位符。
    ///
    /// 只做字符串替换，不做正则匹配 —— 报告是给人读的，
    /// 过度替换会把有用的信息（比如 Steam 库位置）也吃掉。
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;

        // 1. 用户目录（最长匹配优先，避免先被用户名规则切碎）
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        // 2. 机器名与用户名
        //
        // 顺序有讲究：很多人的电脑就是用自己名字命名的（本机就是 ZORA / Zora），
        // 而替换是大小写不敏感的，先替换用户名会把机器名一起吃掉。
        // 同名时只替换一次，并统一用 %USER% —— 它同时覆盖了两个身份，
        // 再区分机器名已经没有意义了。
        var user = Environment.UserName;
        var machine = Environment.MachineName;

        var sameName = !string.IsNullOrWhiteSpace(user)
                       && !string.IsNullOrWhiteSpace(machine)
                       && string.Equals(user, machine, StringComparison.OrdinalIgnoreCase);

        if (sameName)
        {
            if (user.Length >= 3)
            {
                result = result.Replace(user, "%USER%", StringComparison.OrdinalIgnoreCase);
            }

            return result;
        }

        if (!string.IsNullOrWhiteSpace(machine) && machine.Length >= 3)
        {
            result = result.Replace(machine, "%MACHINE%", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3)
        {
            result = result.Replace(user, "%USER%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}

/// <summary>
/// 把报告上传到公开 paste 服务，拿到一个可分享的链接。
///
/// 用 dpaste.com 而不是 pastebin.com：后者要 API key（等于把密钥打进客户端），
/// dpaste 的 v2 接口接受匿名 multipart POST 并直接返回纯文本 URL，实测可用。
/// 服务不可用时只返回错误，绝不阻断主流程 —— 崩溃报告是附加能力。
/// </summary>
public sealed class CrashReportUploader : IDisposable
{
    private const string Endpoint = "https://dpaste.com/api/v2/";

    /// <summary>报告保留天数。dpaste 支持 1/7/365，取 7 天。</summary>
    private const string ExpiryDays = "7";

    private readonly HttpClient _http;

    public CrashReportUploader(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Updates.RmlHttpClient.UserAgent);
    }

    public sealed record UploadResult(bool Success, string? Url, string? Error);

    /// <summary>
    /// 上传报告正文，返回可访问的链接。
    /// </summary>
    /// <param name="report">已经过 <see cref="CrashReportBuilder.Redact"/> 的正文。</param>
    /// <param name="title">paste 标题，便于在服务端列表里识别。</param>
    public async Task<UploadResult> UploadAsync(
        string report,
        string title = "RML Client crash report",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return new UploadResult(false, null, "报告内容为空。");
        }

        try
        {
            using var content = new MultipartFormDataContent
            {
                // dpaste 的字段名固定为 content / syntax / title / expiry_days。
                { new StringContent(report, Encoding.UTF8), "content" },
                { new StringContent("text"), "syntax" },
                { new StringContent(title), "title" },
                { new StringContent(ExpiryDays), "expiry_days" },
            };

            using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new UploadResult(false, null,
                    $"上传失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var url = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();

            // 成功时 dpaste 直接返回一行 URL，不是 JSON。
            return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                   && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
                ? new UploadResult(true, url, null)
                : new UploadResult(false, null, $"服务返回了非预期内容：{Truncate(url, 200)}");
        }
        catch (OperationCanceledException)
        {
            return new UploadResult(false, null, "上传超时或已取消。");
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(false, null, $"网络错误：{ex.Message}");
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => _http.Dispose();
}
