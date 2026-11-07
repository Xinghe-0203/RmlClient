using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using RmlClient.Core.Configuration;
using RmlClient.Core.Diagnostics;

namespace RmlClient.Core.Updates;

/// <summary>GitHub Releases API 返回的资产。</summary>
public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    /// <summary>sha256 digest，形如 <c>sha256:abc…</c>。GitHub 目前只在部分资产上返回。</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

/// <summary>GitHub Releases API 返回的 release。</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();

    public string VersionText => TagName;
}

/// <summary>客户端更新检查结果。</summary>
public sealed record ClientUpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? Changelog,
    string? ReleaseUrl,
    GitHubAsset? Installer,
    string? Error)
{
    public bool HasInstaller => Installer is not null;
}

/// <summary>
/// 客户端自更新：从本项目的 GitHub Releases 拉取最新版本。
///
/// 与 coremod 更新（<see cref="CoreUpdater"/>，走 raftmodding.com）完全独立：
/// 那条链路更新的是「RML 的 mod 加载器」，这条链路更新的是「本客户端自己」。
///
/// 设计取舍：
///   - 只用公开 API，不需要 token（客户端不能内置凭据）
///   - 下载地址仍走白名单校验，且白名单只包含 GitHub 的域名
///   - 优先使用 GitHub 提供的 <c>digest</c>（sha256）做完整性校验；
///     老资产没有 digest 时退化为「只校验长度」，并在结果里如实说明
/// </summary>
public sealed class ClientUpdater
{
    private const string ApiBase = "https://api.github.com";

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    private readonly RmlPaths _paths;
    private readonly HttpClient _http;

    public ClientUpdater(RmlPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;

        var effectiveHandler = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions =
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                      | System.Security.Authentication.SslProtocols.Tls13,
            },
        };

        _http = new HttpClient(effectiveHandler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        // GitHub API 强制要求 User-Agent，否则直接 403。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(RmlHttpClient.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static string LatestReleaseUrl =>
        $"{ApiBase}/repos/{ClientVersion.GitHubOwner}/{ClientVersion.GitHubRepo}/releases/latest";

    public static string AllReleasesUrl =>
        $"{ApiBase}/repos/{ClientVersion.GitHubOwner}/{ClientVersion.GitHubRepo}/releases?per_page=20";

    /// <summary>检查是否有新版本。永不抛异常（除取消）。</summary>
    public async Task<ClientUpdateStatus> CheckAsync(
        string currentVersion = ClientVersion.Current,
        bool includePrerelease = false,
        CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);

            if (release is null)
            {
                return new ClientUpdateStatus(currentVersion, null, false, null, null, null,
                    "GitHub 返回了空响应。");
            }

            if (release.Prerelease && !includePrerelease)
            {
                return new ClientUpdateStatus(currentVersion, release.TagName, false, release.Body,
                    release.HtmlUrl, null, null);
            }

            var available = ClientVersion.IsNewer(release.TagName, currentVersion);
            var installer = PickInstaller(release.Assets);

            return new ClientUpdateStatus(
                currentVersion,
                release.TagName,
                available,
                string.IsNullOrWhiteSpace(release.Body) ? null : release.Body,
                release.HtmlUrl,
                available ? installer : null,
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or NotSupportedException)
        {
            return new ClientUpdateStatus(currentVersion, null, false, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// 下载安装包到缓存目录。返回落地文件路径。
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        GitHubAsset asset,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsTrustedUrl(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException(
                $"拒绝从不信任的地址下载：{asset.BrowserDownloadUrl}");
        }

        _paths.EnsureDirectories();
        var target = Path.Combine(_paths.CacheTemporary, asset.Name);
        var part = target + ".part";

        using (var response = await _http
                   .GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var total = response.Content.Headers.ContentLength ?? asset.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(part, FileMode.Create, FileAccess.Write,
                             FileShare.None, bufferSize: 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }
        }

        // 有 digest 就校验，没有就只比对长度——两种情况都在返回值里如实反映。
        if (TryParseSha256Digest(asset.Digest, out var expected))
        {
            var actual = await HashUtil.Sha256FileAsync(part, ct).ConfigureAwait(false);
            if (!HashUtil.Matches(expected, actual))
            {
                TryDelete(part);
                throw new InvalidDataException(
                    $"安装包校验失败（期望 {expected}，实际 {actual}），已丢弃。");
            }
        }
        else if (asset.Size > 0 && new FileInfo(part).Length != asset.Size)
        {
            TryDelete(part);
            throw new InvalidDataException("安装包大小与 Release 声明不一致，已丢弃。");
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(part, target);
        return target;
    }

    /// <summary>启动安装包。调用方随后应自行退出，让安装程序替换文件。</summary>
    public static bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static GitHubAsset? PickInstaller(IEnumerable<GitHubAsset> assets) =>
        assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
        ?? assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseSha256Digest(string? digest, out string hex)
    {
        hex = string.Empty;

        if (string.IsNullOrWhiteSpace(digest))
        {
            return false;
        }

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        hex = value.ToUpperInvariant();
        return true;
    }

    private static bool IsTrustedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && AllowedHosts.Contains(uri.Host)
               && uri.IdnHost.All(c => c < 128);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
