namespace RmlClient.Core.Updates;

/// <summary>
/// 客户端自身的版本信息。
///
/// 与 <see cref="RmlPaths"/> 里的官方常量分开：这里是「本客户端」的版本，
/// 更新走 GitHub Releases，而 coremod / assets 更新走 raftmodding.com。
/// </summary>
public static class ClientVersion
{
    /// <summary>
    /// 当前版本。发版时与 <c>RmlClient.App.csproj</c> 的 <c>&lt;Version&gt;</c>、
    /// <c>installer\RmlClient.iss</c> 的 <c>AppVersion</c> 一起改。
    /// </summary>
    public const string Current = "1.0.1";

    /// <summary>仓库坐标，用于拼 GitHub API 与 Release 下载地址。</summary>
    public const string GitHubOwner = "Xinghe-0203";

    public const string GitHubRepo = "RmlClient";

    public static string RepoUrl => $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    public static string ReleasesUrl => $"{RepoUrl}/releases";

    /// <summary>把 "1.2.3" / "v1.2.3" / "1.2.3.4" 解析成可比较的四段整数。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        // 去掉预发布后缀（1.2.0-beta.1 → 1.2.0）与构建元数据（1.2.0+abc → 1.2.0）。
        var cut = trimmed.IndexOfAny(new[] { '-', '+' });
        if (cut > 0)
        {
            trimmed = trimmed[..cut];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary><paramref name="candidate"/> 是否比 <paramref name="current"/> 新。</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var candidateVersion))
        {
            return false;
        }

        if (!TryParse(current, out var currentVersion))
        {
            return true;
        }

        return candidateVersion > currentVersion;
    }
}
