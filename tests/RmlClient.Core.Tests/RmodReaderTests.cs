using System.IO.Compression;
using System.Text;
using RmlClient.Core.Mods;

namespace RmlClient.Core.Tests;

public class RmodReaderTests
{
    private static string CreateRmod(string path, string? json, bool includeModInfo = true)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        if (includeModInfo && json is not null)
        {
            var entry = archive.CreateEntry("modinfo.json");
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(json);
            entryStream.Write(bytes, 0, bytes.Length);
        }

        // 顺带塞一个无关文件，确认读取只认 modinfo.json。
        var other = archive.CreateEntry("readme.txt");
        using var otherStream = other.Open();
        var otherBytes = Encoding.UTF8.GetBytes("hello");
        otherStream.Write(otherBytes, 0, otherBytes.Length);

        return path;
    }

    private const string SampleJson = """
        {
          "name": "Better Trees",
          "author": "Someone",
          "description": "Makes trees better.",
          "version": "2.1.0",
          "license": "MIT",
          "updateUrl": "https://www.raftmodding.com/api/v1/mods/better-trees/version.txt",
          "icon": "icon.png"
        }
        """;

    [Fact]
    public void ReadModInfo_ParsesFields()
    {
        using var temp = new TempFolder();
        var path = CreateRmod(temp.Combine("mod.rmod"), SampleJson);

        var info = RmodReader.ReadModInfo(path);

        Assert.Equal("Better Trees", info.Name);
        Assert.Equal("Someone", info.Author);
        Assert.Equal("2.1.0", info.Version);
        Assert.Equal("icon.png", info.Icon);
    }

    [Fact]
    public void ReadModInfo_MissingModInfoThrows()
    {
        using var temp = new TempFolder();
        var path = CreateRmod(temp.Combine("mod.rmod"), json: null, includeModInfo: false);

        var ex = Assert.Throws<InvalidDataException>(() => RmodReader.ReadModInfo(path));
        Assert.Contains("modinfo.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadModInfo_NotAZipThrows()
    {
        using var temp = new TempFolder();
        var path = temp.WriteFile("bad.rmod", "this is not a zip");

        Assert.Throws<InvalidDataException>(() => RmodReader.ReadModInfo(path));
    }

    [Fact]
    public void ReadModInfo_HandlesNestedAndBackslashPaths()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("nested.rmod");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // 有些打包工具会写成 .\modinfo.json，官方做了 Replace("\\","/") 后再比较。
            var entry = archive.CreateEntry("modinfo.json");
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("""{"name":"Nested","version":"1.0"}""");
            entryStream.Write(bytes, 0, bytes.Length);
        }

        var info = RmodReader.ReadModInfo(path);

        Assert.Equal("Nested", info.Name);
    }

    [Fact]
    public void TryReadImage_ReturnsBytesForExistingEntry()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("withicon.rmod");
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("icon.png");
            using var entryStream = entry.Open();
            entryStream.Write(payload, 0, payload.Length);
        }

        Assert.Equal(payload, RmodReader.TryReadImage(path, "icon.png"));
    }

    [Fact]
    public void TryReadImage_ReturnsNullForMissingOrInvalidName()
    {
        using var temp = new TempFolder();
        var path = CreateRmod(temp.Combine("mod.rmod"), SampleJson);

        Assert.Null(RmodReader.TryReadImage(path, null));
        Assert.Null(RmodReader.TryReadImage(path, "  "));
        Assert.Null(RmodReader.TryReadImage(path, "nope.png"));
    }

    [Fact]
    public void TryReadImage_RejectsOversizedEntry()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("big.rmod");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("big.png");
            using var entryStream = entry.Open();
            entryStream.Write(new byte[2048], 0, 2048);
        }

        Assert.Null(RmodReader.TryReadImage(path, "big.png", maxBytes: 1024));
    }

    [Fact]
    public void LooksLikeRmod_DetectsZipSignature()
    {
        using var temp = new TempFolder();

        Assert.True(RmodReader.LooksLikeRmod(CreateRmod(temp.Combine("a.rmod"), SampleJson)));
        Assert.False(RmodReader.LooksLikeRmod(temp.WriteFile("b.rmod", "nope")));
        Assert.False(RmodReader.LooksLikeRmod(temp.Combine("missing.rmod")));
    }

    [Fact]
    public void ReadFolderModInfo_ReadsManifestFromDirectory()
    {
        using var temp = new TempFolder();
        temp.WriteFile("MyMod/modinfo.json", SampleJson);

        var info = RmodReader.ReadFolderModInfo(temp.Combine("MyMod"));

        Assert.Equal("Better Trees", info.Name);
    }

    [Fact]
    public void ReadFolderModInfo_MissingDirectoryThrows()
    {
        using var temp = new TempFolder();

        Assert.Throws<InvalidDataException>(() => RmodReader.ReadFolderModInfo(temp.Combine("nope")));
    }

    [Fact]
    public void TryReadFolderImage_RefusesPathTraversal()
    {
        using var temp = new TempFolder();
        temp.WriteFile("Mod/modinfo.json", SampleJson);
        temp.WriteFile("secret.png", "secret");

        // modinfo.json 里的 icon 是包内相对路径，不能拿它跳出 mod 目录。
        Assert.Null(RmodReader.TryReadFolderImage(temp.Combine("Mod"), "../secret.png"));
    }

    [Fact]
    public void TryReadFolderImage_ReadsFileInsideFolder()
    {
        using var temp = new TempFolder();
        temp.WriteFile("Mod/modinfo.json", SampleJson);
        var image = temp.WriteFile("Mod/icon.png", "icon-bytes");

        Assert.NotNull(RmodReader.TryReadFolderImage(temp.Combine("Mod"), "icon.png"));
        Assert.Equal(File.ReadAllBytes(image), RmodReader.TryReadFolderImage(temp.Combine("Mod"), "icon.png"));
    }
}

public class ModEntryTests
{
    private static ModEntry Entry(string fileName, string? updateUrl = null) => new()
    {
        FilePath = @"C:\game\mods\" + fileName,
        FileName = fileName,
        IsEnabled = true,
        FileSize = 1,
        Info = new ModInfo { Name = "X", UpdateUrl = updateUrl },
    };

    [Theory]
    [InlineData("https://www.raftmodding.com/api/v1/mods/better-trees/version.txt", "better-trees")]
    [InlineData("https://www.raftmodding.com/api/v1/mods/morestorages/version.txt", "morestorages")]
    public void MarketSlug_ExtractsSlugFromUpdateUrl(string url, string expected) =>
        Assert.Equal(expected, Entry("a.rmod", url).MarketSlug);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/whatever")]
    [InlineData("https://www.raftmodding.com/api/v1/mods/your-mod-slug-here/version.txt")]
    public void MarketSlug_ReturnsNullWhenAbsent(string? url) =>
        Assert.Null(Entry("a.rmod", url).MarketSlug);

    [Fact]
    public void DisplayName_PrefersInfoName()
    {
        Assert.Equal("X", Entry("modinstaller.foo.rmod").DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToFileName()
    {
        var entry = new ModEntry
        {
            FilePath = @"C:\game\mods\modinstaller.foo.rmod",
            FileName = "modinstaller.foo.rmod",
            IsEnabled = true,
            FileSize = 1,
        };

        Assert.Equal("modinstaller.foo", entry.DisplayName);
    }
}
