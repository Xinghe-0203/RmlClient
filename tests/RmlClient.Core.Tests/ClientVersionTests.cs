using RmlClient.Core.Updates;

namespace RmlClient.Core.Tests;

public class ClientVersionTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("V2.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    // 预发布与构建元数据要能剥掉再比较，否则 GitHub tag 里的 v1.0.1-beta 会被判成不新。
    [InlineData("1.0.1-beta.1", "1.0.0", true)]
    [InlineData("1.0.1+abc123", "1.0.0", true)]
    public void IsNewer_ComparesVersions(string candidate, string current, bool expected) =>
        Assert.Equal(expected, ClientVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    public void IsNewer_UnparsableCandidateIsNeverNewer(string? candidate) =>
        Assert.False(ClientVersion.IsNewer(candidate, "1.0.0"));

    [Fact]
    public void IsNewer_UnparsableCurrentMeansCandidateWins()
    {
        // 本地版本读不出来时宁可提示更新，也好过永远不更新。
        Assert.True(ClientVersion.IsNewer("1.0.0", "?"));
        Assert.True(ClientVersion.IsNewer("1.0.0", null));
    }

    [Fact]
    public void TryParse_HandlesMissingComponents()
    {
        Assert.True(ClientVersion.TryParse("1", out var v));
        Assert.Equal(new Version(1, 0), v);

        Assert.True(ClientVersion.TryParse("1.2", out v));
        Assert.Equal(new Version(1, 2), v);
    }

    [Fact]
    public void Current_IsParsable()
    {
        Assert.True(ClientVersion.TryParse(ClientVersion.Current, out var v));
        Assert.True(v >= new Version(1, 0, 0));
    }

    [Fact]
    public void RepoUrl_PointsAtGitHub()
    {
        Assert.Equal(
            $"https://github.com/{ClientVersion.GitHubOwner}/{ClientVersion.GitHubRepo}",
            ClientVersion.RepoUrl);
        Assert.EndsWith("/releases", ClientVersion.ReleasesUrl, StringComparison.Ordinal);
    }
}
