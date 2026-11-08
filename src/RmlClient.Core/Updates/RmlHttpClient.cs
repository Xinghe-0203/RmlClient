using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RmlClient.Core.Configuration;

namespace RmlClient.Core.Updates;

/// <summary>
/// 与 raftmodding.com 通信的 HTTP 客户端。
///
/// 相对官方的改进：
///   - 全程 <see cref="HttpClient"/> + TLS 1.2/1.3，**不**禁用证书校验
///     （官方 coremod 的 <c>HttpCertHandler.ValidateCertificate</c> 直接返回 true）
///   - 所有下载都经过 <see cref="Diagnostics.UrlGuard"/> 主机白名单校验
///   - 禁止自动重定向（官方 WebClient 默认跟随，跳转后不再校验域名）
///   - 明确的超时与取消
/// </summary>
public sealed class RmlHttpClient : IDisposable
{
    public const string UserAgent = "RMLClient/1.0 (Windows; RaftModLoader)";

    private readonly HttpClient _http;

    public RmlHttpClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var effectiveHandler = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions =
            {
                // 明确启用 TLS 1.2/1.3，绝不放宽校验。
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                      | System.Security.Authentication.SslProtocols.Tls13,
            },
        };

        _http = new HttpClient(effectiveHandler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
        GetStringAsync(url, TimeSpan.FromSeconds(20), ct);

    public async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}：{url}",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载到文件，带进度回调。先写 <c>.part</c> 再原子改名，
    /// 避免中断留下一个看起来完整的坏文件（官方直接覆盖写目标文件）。
    ///
    /// 支持断点续传：如果 <c>.part</c> 已经存在且有内容，会带上 <c>Range: bytes=N-</c>
    /// 从断点继续，而不是从头重下。服务器不认 Range（返回 200 而不是 206）时
    /// 会自动退回从头写，不会把两段数据拼成一个坏文件。
    /// </summary>
    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Diagnostics.UrlGuard.IsTrustedDownloadUrl(url))
        {
            throw new InvalidOperationException(
                $"拒绝从不信任的地址下载：{url}（允许的主机：{Diagnostics.UrlGuard.DescribeTrustedHosts()}）");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partPath = destinationPath + ".part";
        await DownloadToPartAsync(url, partPath, progress, ct).ConfigureAwait(false);

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(partPath, destinationPath);
    }

    /// <summary>
    /// 下载到 <c>.part</c>，能从已有内容续传。抽出来是为了让重定向递归也走同一套续传逻辑。
    /// </summary>
    private async Task DownloadToPartAsync(
        string url,
        string partPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        // 已有 .part 就从它的长度接着下。
        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // 手动跟随一次重定向，但目标必须仍在白名单内。
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var location = response.Headers.Location;
            if (location is null)
            {
                throw new HttpRequestException("服务器返回重定向但没有 Location 头。");
            }

            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri(url), location);
            if (!Diagnostics.UrlGuard.IsTrustedDownloadUrl(absolute.ToString()))
            {
                throw new InvalidOperationException(
                    $"拒绝跟随跳转到不信任的地址：{absolute}（允许的主机：{Diagnostics.UrlGuard.DescribeTrustedHosts()}）");
            }

            await DownloadToPartAsync(absolute.ToString(), partPath, progress, ct).ConfigureAwait(false);
            return;
        }

        // 服务器不认 Range 就会回 200 并从头发，此时必须丢掉旧数据重新写，
        // 否则新数据会接在半截旧数据后面，得到一个体积对但内容错的文件。
        var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
        var append = isPartial && resumeFrom > 0;
        if (!append)
        {
            resumeFrom = 0;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                inner: null,
                response.StatusCode);
        }

        // 206 的 Content-Length 只是剩余部分，总长度要从 Content-Range 里取。
        long? total = isPartial
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, bufferSize: 1024 * 128, useAsync: true))
        {
            var buffer = new byte[1024 * 128];
            long received = resumeFrom;
            int read;

            progress?.Report(new DownloadProgress(received, total));

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        progress?.Report(new DownloadProgress(
            new FileInfo(partPath).Length,
            total ?? new FileInfo(partPath).Length));
    }

    /// <summary>
    /// 清掉未完成的 <c>.part</c>。取消下载或确定要重下时调用，
    /// 否则下次会从这段可疑数据继续。
    /// </summary>
    public static void DiscardPartialDownload(string destinationPath)
    {
        var partPath = destinationPath + ".part";
        try
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就算了，下次续传时 Content-Range 对不上会退回重写。
        }
    }

    /// <summary>是否有一个可以续传的半成品。</summary>
    public static bool HasPartialDownload(string destinationPath) =>
        File.Exists(destinationPath + ".part") && new FileInfo(destinationPath + ".part").Length > 0;

    public void Dispose() => _http.Dispose();
}

public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1)
        : 0;

    public string HumanReadable => TotalBytes is > 0
        ? $"{Format(BytesReceived)} / {Format(TotalBytes.Value)}"
        : Format(BytesReceived);

    private static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>
/// 三个官方接口的封装。
///
/// 端点与参数完全照抄逆向结果：
///   - <c>{fastdl}/data/launcher.php?branch={branch}</c>
///   - <c>{fastdl}/data/client.php?branch={branch}</c>
///   - <c>{site}/api/v1/mods</c>
/// </summary>
public sealed class RmlApi
{
    private readonly RmlHttpClient _http;

    public RmlApi(RmlHttpClient http) => _http = http;

    public static string LauncherManifestUrl(string branch) =>
        $"{RmlPaths.FastDlBaseUrl}/data/launcher.php?branch={Uri.EscapeDataString(branch)}";

    public static string ClientManifestUrl(string branch) =>
        $"{RmlPaths.FastDlBaseUrl}/data/client.php?branch={Uri.EscapeDataString(branch)}";

    public static string ModListUrl() => $"{RmlPaths.SiteBaseUrl}/api/v1/mods";

    public static string ModDetailUrl(string modId) =>
        $"{RmlPaths.SiteBaseUrl}/api/v1/mods/{Uri.EscapeDataString(modId)}";

    public async Task<LauncherManifest> GetLauncherManifestAsync(string branch, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(LauncherManifestUrl(branch), TimeSpan.FromSeconds(15), ct)
            .ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("launcher.php 返回了空响应。");

        if (manifest.HasError)
        {
            throw new InvalidDataException($"服务器拒绝分支 '{branch}'：{manifest.Error}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Hash))
        {
            throw new InvalidDataException("launcher.php 响应缺少 version 或 hash 字段。");
        }

        return manifest;
    }

    public async Task<CoreManifest> GetCoreManifestAsync(string branch, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(ClientManifestUrl(branch), TimeSpan.FromSeconds(15), ct)
            .ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<CoreManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("client.php 返回了空响应。");

        if (manifest.HasError)
        {
            throw new InvalidDataException($"服务器拒绝分支 '{branch}'：{manifest.Error}");
        }

        return manifest;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
