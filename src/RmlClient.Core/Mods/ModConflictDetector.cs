using System.IO.Compression;

namespace RmlClient.Core.Mods;

/// <summary>
/// 模组冲突检测。
///
/// 设计文档 §6.4 原本打算用 <c>modinfo.json</c> 的 <c>excludedFiles</c> 做冲突判断，
/// 但那是个误解：<c>excludedFiles</c> 是**打包时排除**的规则
/// （见官方 <c>FolderModHandler.cs:78</c>，默认值 <c>{"*.csproj","*.csproj.user","*.cache","*.sln"}</c>），
/// 与运行时资源冲突毫无关系。
///
/// 所以这里改用两个真正会出问题的判据：
///   1. **重名**：两个 mod 的 <c>modinfo.json</c> 里 <c>name</c> 相同。
///      官方按 name 建索引（<c>ModManagerPage.cs</c> 的字典），重名会让其中一个被顶掉，
///      表现是「装了两个，游戏里只看到一个」。
///   2. **文件重叠**：两个 mod 包里含同名文件。Unity 的资源与程序集加载不看 mod 边界，
///      同名 DLL / 同名资源会被后加载的覆盖，表现是随机崩溃或功能互相打断。
///
/// 检测只读包内条目名，不解压内容，因此对 20MB 的大包也很快。
/// </summary>
public static class ModConflictDetector
{
    /// <summary>一条冲突记录。</summary>
    /// <param name="Kind">冲突类型。</param>
    /// <param name="Key">冲突的对象：重名时是 mod 名，文件重叠时是相对路径。</param>
    /// <param name="Mods">涉及到的 mod（两个以上）。</param>
    public sealed record ModConflict(ModConflictKind Kind, string Key, IReadOnlyList<ModEntry> Mods)
    {
        /// <summary>用于 UI 显示的标题。</summary>
        public string Title => Kind == ModConflictKind.DuplicateName ? Key : Path.GetFileName(Key);

        /// <summary>涉及 mod 的显示名，用顿号连接。</summary>
        public string ModNames => string.Join("、", Mods.Select(m => m.DisplayName));
    }

    public enum ModConflictKind
    {
        /// <summary>两个 mod 的 modinfo.json 里 name 相同。</summary>
        DuplicateName = 0,

        /// <summary>两个 mod 包含同名文件。</summary>
        OverlappingFiles = 1,
    }

    /// <summary>
    /// 检测给定 mod 列表里的冲突。只检查已启用的 mod —— 禁用的不会被加载，不会真冲突。
    /// </summary>
    public static IReadOnlyList<ModConflict> Detect(IReadOnlyList<ModEntry> mods)
    {
        var enabled = mods.Where(m => m.IsEnabled && m.ParseError is null).ToList();
        var conflicts = new List<ModConflict>();

        conflicts.AddRange(DetectDuplicateNames(enabled));
        conflicts.AddRange(DetectOverlappingFiles(enabled));

        return conflicts;
    }

    private static IEnumerable<ModConflict> DetectDuplicateNames(List<ModEntry> mods)
    {
        var groups = mods
            .Where(m => !string.IsNullOrWhiteSpace(m.Info?.Name))
            .GroupBy(m => m.Info!.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            yield return new ModConflict(
                ModConflictKind.DuplicateName,
                group.Key,
                group.ToList());
        }
    }

    private static IEnumerable<ModConflict> DetectOverlappingFiles(List<ModEntry> mods)
    {
        // 路径 → 含该路径的 mod 列表。
        var owners = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var path in EnumerateFileEntries(mod))
            {
                if (!owners.TryGetValue(path, out var list))
                {
                    list = new List<ModEntry>(2);
                    owners[path] = list;
                }

                if (!list.Contains(mod))
                {
                    list.Add(mod);
                }
            }
        }

        // 同一个文件被两个以上 mod 提供才算出问题。
        // modinfo.json 与图标这类每个包都有的元数据要排除，
        // 否则 20 个 mod 会报出 20 条「冲突」，把真正的冲突淹掉。
        foreach (var (path, list) in owners)
        {
            if (list.Count < 2 || IsIgnorable(path))
            {
                continue;
            }

            yield return new ModConflict(ModConflictKind.OverlappingFiles, path, list);
        }
    }

    private static bool IsIgnorable(string relativePath)
    {
        // 每个 .rmod 都必须带 modinfo.json，图标/横幅也是各包独立的展示资源，
        // 这些同名不算冲突。
        if (string.Equals(relativePath, "modinfo.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 只有真正会被 Unity 加载的东西才可能冲突：程序集、资源包、贴图。
        // 源码/工程文件是打包时顺带塞进来的（官方默认 excludedFiles 就排除了
        // *.csproj/*.sln），两个 mod 各自带一个 Patches.cs 完全无害 ——
        // 实测 Extra Settings API 与 Minimap 都含 Patches.cs，但运行时毫无影响。
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();

        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".ico" or ".bmp" or ".gif" => true,
            ".cs" or ".csproj" or ".sln" or ".user" or ".cache" => true,
            ".md" or ".txt" or ".json" or ".xml" or ".pdb" => true,
            _ => false,
        };
    }

    /// <summary>
    /// 列出 mod 提供的文件条目。
    /// 压缩包读 zip 目录；文件夹 mod（.lnk）递归列文件；解析失败或读取异常时返回空。
    /// </summary>
    private static IEnumerable<string> EnumerateFileEntries(ModEntry mod)
    {
        if (mod.ShortcutTarget is { Length: > 0 } folder)
        {
            return EnumerateFolderEntries(folder);
        }

        try
        {
            using var stream = new FileStream(mod.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            return archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => e.FullName.Replace('\\', '/'))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateFolderEntries(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        try
        {
            // 与官方 FolderModHandler 的跳过规则对齐：编译产物目录里的东西不参与运行。
            var skipped = new[] { "bin", "obj", "properties", ".vs", ".git" };

            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    var relative = Path.GetRelativePath(folder, path);
                    var first = relative.Split(Path.DirectorySeparatorChar, '/')[0];
                    return !skipped.Contains(first, StringComparer.OrdinalIgnoreCase);
                })
                .Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
