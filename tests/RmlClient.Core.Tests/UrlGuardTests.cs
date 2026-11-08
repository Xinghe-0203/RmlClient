using RmlClient.Core.Diagnostics;

namespace RmlClient.Core.Tests;

public class UrlGuardTests
{
    [Theory]
    [InlineData("https://fastdl.raftmodding.com/data/client.php?branch=public")]
    [InlineData("https://www.raftmodding.com/mods/itemspawner/6.2.4/x.rmod")]
    [InlineData("https://raftmodding.com/api/v1/mods")]
    public void TrustedHosts_AreAccepted(string url) => Assert.True(UrlGuard.IsTrustedDownloadUrl(url));

    [Theory]
    // 官方用 StartsWith 比较，这三种都能绕过 —— 本实现必须全部拒绝。
    [InlineData("https://fastdl.raftmodding.com.evil.tld/x.exe")]
    [InlineData("https://fastdl.raftmodding.com@evil.tld/x.exe")]
    [InlineData("https://fastdl.raftmodding.com:8443/x.exe")]
    // 明文与其它协议一律不行。
    [InlineData("http://fastdl.raftmodding.com/x.exe")]
    [InlineData("ftp://fastdl.raftmodding.com/x.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    // 相似但不同的域名。
    [InlineData("https://evilraftmodding.com/x.exe")]
    [InlineData("https://raftmodding.com.evil.tld/x.exe")]
    [InlineData("https://sub.fastdl.raftmodding.com/x.exe")]
    public void UntrustedUrls_AreRejected(string url) => Assert.False(UrlGuard.IsTrustedDownloadUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path.rmod")]
    public void MalformedUrls_AreRejected(string url) => Assert.False(UrlGuard.IsTrustedDownloadUrl(url));

    [Fact]
    public void NullUrl_IsRejected() => Assert.False(UrlGuard.IsTrustedDownloadUrl(null));

    [Fact]
    public void ResolveModDownloadUrl_PrefixesSiteBase()
    {
        var resolved = UrlGuard.ResolveModDownloadUrl("/mods/gravity/1.0.2-/Gravity.rmod");

        Assert.Equal("https://www.raftmodding.com/mods/gravity/1.0.2-/Gravity.rmod", resolved);
        Assert.True(UrlGuard.IsTrustedDownloadUrl(resolved));
    }

    [Fact]
    public void ResolveModDownloadUrl_KeepsAbsoluteUrl()
    {
        const string absolute = "https://www.raftmodding.com/mods/x/x.rmod";

        Assert.Equal(absolute, UrlGuard.ResolveModDownloadUrl(absolute));
    }

    [Fact]
    public void ResolveModDownloadUrl_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => UrlGuard.ResolveModDownloadUrl("  "));
    }

    [Fact]
    public void DescribeTrustedHosts_ListsAllHosts()
    {
        var text = UrlGuard.DescribeTrustedHosts();

        Assert.Contains("fastdl.raftmodding.com", text, StringComparison.Ordinal);
        Assert.Contains("raftmodding.com", text, StringComparison.Ordinal);
    }
}

public class HashUtilTests
{
    [Fact]
    public void Sha512_EmptyString_MatchesKnownDigest()
    {
        // echo -n "" | sha512sum 的标准结果，用来确认没有加 BOM 或换行。
        const string expected =
            "CF83E1357EEFB8BDF1542850D66D8007D620E4050B5715DC83F4A921D36CE9CE" +
            "47D0D13C5D85F2B0FF8318D2877EEC2F63B931BD47417A81A538327AF927DA3E";

        Assert.Equal(expected, HashUtil.Sha512String(string.Empty));
    }

    [Fact]
    public void Sha512String_IsUpperCaseHex()
    {
        var hash = HashUtil.Sha512String("raft");

        Assert.Equal(128, hash.Length);
        Assert.Equal(hash.ToUpperInvariant(), hash);
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigit(c)));
    }

    [Fact]
    public void Sha512File_MatchesSha512StringForSameBytes()
    {
        using var temp = new TempFolder();
        var path = temp.WriteFile("a.txt", "raft");

        Assert.Equal(HashUtil.Sha512String("raft"), HashUtil.Sha512File(path));
    }

    [Fact]
    public async Task Sha512FileAsync_MatchesSyncVersion()
    {
        using var temp = new TempFolder();
        var path = temp.WriteFile("a.txt", new string('x', 100_000));

        Assert.Equal(HashUtil.Sha512File(path), await HashUtil.Sha512FileAsync(path));
    }

    [Fact]
    public void Sha256File_IsStable()
    {
        using var temp = new TempFolder();
        var path = temp.WriteFile("a.txt", "raft");

        var first = HashUtil.Sha256File(path);
        var second = HashUtil.Sha256File(path);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Sha512File_MissingFileThrows()
    {
        using var temp = new TempFolder();

        Assert.Throws<FileNotFoundException>(() => HashUtil.Sha512File(temp.Combine("nope")));
    }

    [Fact]
    public void Matches_IgnoresCaseAndWhitespace()
    {
        const string expected = "ABCDEF0123456789";

        Assert.True(HashUtil.Matches(expected, "abcdef0123456789"));
        Assert.True(HashUtil.Matches($"  {expected}  ", "ABCDEF0123456789 "));
    }

    [Theory]
    [InlineData("ABCDEF", "ABCDEF0")]
    [InlineData("", "ABCDEF")]
    [InlineData("ABCDEF", "")]
    [InlineData(null, "ABCDEF")]
    [InlineData("ABCDEF", null)]
    [InlineData("   ", "ABCDEF")]
    public void Matches_RejectsMismatchOrBlank(string? expected, string? actual) =>
        Assert.False(HashUtil.Matches(expected, actual));
}
