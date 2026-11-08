using System.IO.Compression;
using RmlClient.Core.Mods;

namespace RmlClient.Core.Tests;

/// <summary>
/// 冲突检测的用例。
///
/// 这里要防的是两个方向的错误：
///   - 漏报：两个 mod 真的共用同一个 DLL/资源却没查出来；
///   - 误报：把源码、图标、modinfo.json 这类天然同名的东西报成冲突
///     （实测 Extra Settings API 与 Minimap 都带 Patches.cs，报了反而干扰用户）。
/// </summary>
public sealed class ModConflictDetectorTests : IDisposable
{
    private readonly TempFolder _folder = new("rmlclient-conflict");

    public void Dispose() => _folder.Dispose();

    private ModEntry CreateMod(
        string fileName,
        string name,
        string version = "1.0",
        params string[] files)
    {
        var path = _folder.Combine(fileName);
        var info = $$"""
            {
              "name": "{{name}}",
              "author": "tester",
              "version": "{{version}}",
              "description": "d"
            }
            """;

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "modinfo.json", info);
            foreach (var file in files)
            {
                WriteEntry(archive, file, "x");
            }
        }

        return new ModEntry
        {
            FilePath = path,
            FileName = fileName,
            IsEnabled = true,
            FileSize = new FileInfo(path).Length,
            Info = RmodReader.ReadModInfo(path),
        };
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static ModEntry Disabled(ModEntry entry) => new()
    {
        FilePath = entry.FilePath,
        FileName = entry.FileName,
        IsEnabled = false,
        FileSize = entry.FileSize,
        Info = entry.Info,
    };

    [Fact]
    public void Detect_NoConflicts_WhenModsAreDistinct()
    {
        var a = CreateMod("a.rmod", "Mod A", "1.0", "a.dll", "data/a.unity3d");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "b.dll", "data/b.unity3d");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Detect_DuplicateName_ReportsBothMods()
    {
        var a = CreateMod("a.rmod", "Same Name", "1.0", "a.dll");
        var b = CreateMod("b.rmod", "Same Name", "2.0", "b.dll");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(ModConflictDetector.ModConflictKind.DuplicateName, conflict.Kind);
        Assert.Equal("Same Name", conflict.Key);
        Assert.Equal(2, conflict.Mods.Count);
    }

    [Fact]
    public void Detect_DuplicateName_IsCaseInsensitive()
    {
        var a = CreateMod("a.rmod", "Stack Boost", "1.0", "a.dll");
        var b = CreateMod("b.rmod", "stack boost", "2.0", "b.dll");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        Assert.Single(conflicts);
    }

    [Fact]
    public void Detect_OverlappingAssembly_IsReported()
    {
        var a = CreateMod("a.rmod", "Mod A", "1.0", "0Harmony.dll");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "0Harmony.dll");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(ModConflictDetector.ModConflictKind.OverlappingFiles, conflict.Kind);
        Assert.Equal("0Harmony.dll", conflict.Key);
        Assert.Equal(2, conflict.Mods.Count);
    }

    [Fact]
    public void Detect_OverlappingResource_IsReported()
    {
        var a = CreateMod("a.rmod", "Mod A", "1.0", "assets/shared.unity3d");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "assets/shared.unity3d");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        Assert.Single(conflicts);
    }

    [Fact]
    public void Detect_SourceFiles_AreNotConflicts()
    {
        // 实测场景：Extra Settings API 与 Minimap 都带 Patches.cs，但互不影响。
        var a = CreateMod("a.rmod", "Mod A", "1.0", "Patches.cs", "a.csproj", "README.md");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "Patches.cs", "b.csproj", "README.md");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Detect_ImagesAndModInfo_AreNotConflicts()
    {
        var a = CreateMod("a.rmod", "Mod A", "1.0", "icon.png", "banner.jpg");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "icon.png", "banner.jpg");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Detect_DisabledModsAreIgnored()
    {
        var a = CreateMod("a.rmod", "Mod A", "1.0", "0Harmony.dll");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "0Harmony.dll");

        var conflicts = ModConflictDetector.Detect(new[] { a, Disabled(b) });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Detect_BrokenArchive_DoesNotThrow()
    {
        var broken = _folder.Combine("broken.rmod");
        File.WriteAllText(broken, "not a zip");

        var entry = new ModEntry
        {
            FilePath = broken,
            FileName = "broken.rmod",
            IsEnabled = true,
            FileSize = 9,
            ParseError = "包内缺少 modinfo.json。",
        };

        var conflicts = ModConflictDetector.Detect(new[] { entry });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Detect_ThreeModsSharingFile_ReportsAllThree()
    {
        var a = CreateMod("a.rmod", "Mod A", "1.0", "shared.dll");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "shared.dll");
        var c = CreateMod("c.rmod", "Mod C", "1.0", "shared.dll");

        var conflicts = ModConflictDetector.Detect(new[] { a, b, c });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(3, conflict.Mods.Count);
    }

    [Fact]
    public void Detect_OverlappingFiles_ExcludesBackslashesVariants()
    {
        // 包内条目可能用反斜杠，归一化后应该仍然认出是同一个文件。
        var a = CreateMod("a.rmod", "Mod A", "1.0", "lib/shared.dll");
        var b = CreateMod("b.rmod", "Mod B", "1.0", "lib\\shared.dll");

        var conflicts = ModConflictDetector.Detect(new[] { a, b });

        Assert.Single(conflicts);
    }

    [Fact]
    public void Detect_FolderMod_ReportsOverlapWithArchive()
    {
        var folder = _folder.Combine("folder-mod");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "modinfo.json"),
            """{ "name": "Folder Mod", "author": "t", "version": "1.0" }""");
        File.WriteAllText(Path.Combine(folder, "shared.dll"), "x");

        var folderEntry = new ModEntry
        {
            FilePath = _folder.Combine("folder-mod.lnk"),
            FileName = "folder-mod.lnk",
            IsEnabled = true,
            FileSize = 10,
            ShortcutTarget = folder,
            Info = RmodReader.ReadFolderModInfo(folder),
        };

        var archive = CreateMod("a.rmod", "Archive Mod", "1.0", "shared.dll");

        var conflicts = ModConflictDetector.Detect(new[] { folderEntry, archive });

        var conflict = Assert.Single(conflicts);
        Assert.Equal("shared.dll", conflict.Key);
    }
}
