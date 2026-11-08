using System.IO.Compression;
using System.Text;
using RmlClient.Core.Mods;

namespace RmlClient.Core.Tests;

public class ModRepositoryTests
{
    private const string Manifest = """{"name":"Test Mod","version":"1.0"}""";

    private static void WriteRmod(string path, string name = "Test Mod", string version = "1.0")
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("modinfo.json");
        using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes($$"""{"name":"{{name}}","version":"{{version}}"}""");
        entryStream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Scan_ReturnsEmptyWhenFolderMissing()
    {
        using var temp = new TempFolder();
        var repo = new ModRepository(temp.Combine("mods"));

        Assert.Empty(repo.Scan());
        Assert.False(repo.Exists);
    }

    [Fact]
    public void Scan_FindsEnabledAndDisabledRmod()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        WriteRmod(temp.Combine("mods", "a.rmod"), "Alpha");
        WriteRmod(temp.Combine("mods", "b.rmod.disabled"), "Beta");
        File.WriteAllText(temp.Combine("mods", "notes.txt"), "ignored");

        var entries = new ModRepository(temp.Combine("mods")).Scan();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.DisplayName == "Alpha" && e.IsEnabled);
        Assert.Contains(entries, e => e.DisplayName == "Beta" && !e.IsEnabled);
    }

    [Fact]
    public void Scan_DoesNotSkipModInstallerPrefix()
    {
        // 官方启动器把 mod 装成 modinstaller.<slug>.rmod，coremod 照样加载
        // （ModManagerPage.cs:121），所以扫描不能把它们过滤掉。
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        WriteRmod(temp.Combine("mods", "modinstaller.itemspawner.rmod"), "Item Spawner");

        var entries = new ModRepository(temp.Combine("mods")).Scan();

        Assert.Single(entries);
        Assert.Equal("Item Spawner", entries[0].DisplayName);
    }

    [Fact]
    public void Scan_RecordsParseErrorForBrokenPackage()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        File.WriteAllText(temp.Combine("mods", "broken.rmod"), "not a zip");

        var entries = new ModRepository(temp.Combine("mods")).Scan();

        Assert.Single(entries);
        Assert.NotNull(entries[0].ParseError);
    }

    [Fact]
    public void Scan_SortsByDisplayName()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        WriteRmod(temp.Combine("mods", "c.rmod"), "Charlie");
        WriteRmod(temp.Combine("mods", "a.rmod"), "Alpha");
        WriteRmod(temp.Combine("mods", "b.rmod"), "Bravo");

        var names = new ModRepository(temp.Combine("mods")).Scan().Select(e => e.DisplayName).ToList();

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, names);
    }

    [Fact]
    public void SetEnabled_RenamesBothWays()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        WriteRmod(temp.Combine("mods", "a.rmod"));

        var repo = new ModRepository(temp.Combine("mods"));
        var entry = repo.Scan().Single();

        Assert.True(repo.SetEnabled(entry, false).Success);
        Assert.True(File.Exists(temp.Combine("mods", "a.rmod.disabled")));
        Assert.False(File.Exists(temp.Combine("mods", "a.rmod")));

        var disabled = repo.Scan().Single();
        Assert.False(disabled.IsEnabled);

        Assert.True(repo.SetEnabled(disabled, true).Success);
        Assert.True(File.Exists(temp.Combine("mods", "a.rmod")));
        Assert.False(File.Exists(temp.Combine("mods", "a.rmod.disabled")));
    }

    [Fact]
    public void SetEnabled_IsIdempotent()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        WriteRmod(temp.Combine("mods", "a.rmod"));

        var repo = new ModRepository(temp.Combine("mods"));
        var entry = repo.Scan().Single();

        Assert.True(repo.SetEnabled(entry, true).Success);
        Assert.True(File.Exists(temp.Combine("mods", "a.rmod")));
    }

    [Fact]
    public void SetEnabled_MissingFileFails()
    {
        using var temp = new TempFolder();
        var repo = new ModRepository(temp.Combine("mods"));
        var ghost = new ModEntry
        {
            FilePath = temp.Combine("mods", "ghost.rmod"),
            FileName = "ghost.rmod",
            IsEnabled = true,
            FileSize = 0,
        };

        Assert.False(repo.SetEnabled(ghost, false).Success);
    }

    [Fact]
    public void Install_CopiesAndValidatesPackage()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        var source = temp.Combine("source.rmod");
        WriteRmod(source, "Installed Mod");

        var repo = new ModRepository(temp.Combine("mods"));
        var result = repo.Install(source);

        Assert.True(result.Success);
        Assert.True(File.Exists(temp.Combine("mods", "source.rmod")));
    }

    [Fact]
    public void Install_UsesRequestedTargetFileName()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        var source = temp.Combine("temp-download.rmod");
        WriteRmod(source, "Item Spawner");

        var repo = new ModRepository(temp.Combine("mods"));
        var result = repo.Install(source, "modinstaller.itemspawner.rmod");

        Assert.True(result.Success);
        Assert.True(File.Exists(temp.Combine("mods", "modinstaller.itemspawner.rmod")));
    }

    [Theory]
    [InlineData(@"..\escape.rmod")]
    [InlineData("../escape.rmod")]
    [InlineData(@"C:\Windows\evil.rmod")]
    public void Install_RejectsTargetNameWithPathSeparators(string target)
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        var source = temp.Combine("source.rmod");
        WriteRmod(source);

        var result = new ModRepository(temp.Combine("mods")).Install(source, target);

        Assert.False(result.Success);
        Assert.Contains("非法", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_RejectsNonRmodSource()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        var source = temp.WriteFile("bad.rmod", "nope");

        var result = new ModRepository(temp.Combine("mods")).Install(source);

        Assert.False(result.Success);
    }

    [Fact]
    public void Install_MissingSourceFails()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));

        var result = new ModRepository(temp.Combine("mods")).Install(temp.Combine("nope.rmod"));

        Assert.False(result.Success);
    }

    [Fact]
    public void Install_ReplacesExistingFile()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("mods"));
        WriteRmod(temp.Combine("mods", "target.rmod"), "Old Name");
        var source = temp.Combine("source.rmod");
        WriteRmod(source, "New Name");

        var result = new ModRepository(temp.Combine("mods")).Install(source, "target.rmod");

        Assert.True(result.Success);
        Assert.Equal("New Name", new ModRepository(temp.Combine("mods")).Scan().Single().DisplayName);
    }

    [Fact]
    public void Uninstall_MissingFileFails()
    {
        using var temp = new TempFolder();
        var repo = new ModRepository(temp.Combine("mods"));
        var ghost = new ModEntry
        {
            FilePath = temp.Combine("mods", "ghost.rmod"),
            FileName = "ghost.rmod",
            IsEnabled = true,
            FileSize = 0,
        };

        Assert.False(repo.Uninstall(ghost).Success);
    }
}
