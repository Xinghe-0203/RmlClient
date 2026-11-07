namespace RmlClient.Core.Diagnostics;

/// <summary>
/// 下载来源白名单。
///
/// 官方的实现是 <c>url.StartsWith("https://fastdl.raftmodding.com")</c>（见
/// <c>LauncherUpdater\Main.cs:281</c>），这是可绕过的字符串前缀比较：
///   - <c>https://fastdl.raftmodding.com.evil.tld/x.exe</c> 能通过
///   - <c>https://fastdl.raftmodding.com@evil.tld/x.exe</c> 能通过
///   - 跟随重定向后完全不再校验
///
/// 本实现按 <see cref="Uri"/> 解析出的 scheme + host + port 做精确匹配，
/// 并且明确禁止重定向到白名单外的地址。
/// </summary>
public static class UrlGuard
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "fastdl.raftmodding.com",
        "www.raftmodding.com",
        "raftmodding.com",
    };

    /// <summary>是否为本程序允许下载可执行内容的地址。</summary>
    public static bool IsTrustedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // 只允许默认端口，防止 https://host:8443 这类非常规端口被利用。
        if (!uri.IsDefaultPort)
        {
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            return false;
        }

        // Uri.Host 已做规范化（去掉 userinfo、末尾点、大小写），
        // 但为了防御 IDN 同形字符，再要求主机名必须是纯 ASCII。
        return uri.IdnHost.All(c => c < 128);
    }

    /// <summary>
    /// 下载 mod 包时更宽松一些：允许 raftmodding.com 站内的相对路径拼成绝对地址。
    /// </summary>
    public static string ResolveModDownloadUrl(string siteRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(siteRelativeUrl))
        {
            throw new ArgumentException("Download URL is empty.", nameof(siteRelativeUrl));
        }

        if (Uri.TryCreate(siteRelativeUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return RmlClient.Core.Configuration.RmlPaths.SiteBaseUrl + siteRelativeUrl;
    }

    /// <summary>把白名单规则表述成人类可读的说明，供错误提示使用。</summary>
    public static string DescribeTrustedHosts() => string.Join(", ", AllowedHosts.OrderBy(h => h));
}
