using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RmlClient.Core.Mods;

/// <summary>
/// .rmod 里的 modinfo.json。
///
/// 字段名与官方 <c>HMLLibrary.ModData.ModInfo</c> 一致（JSON 里是 camelCase）。
/// </summary>
public sealed class ModInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("license")]
    public string License { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("banner")]
    public string? Banner { get; set; }

    [JsonPropertyName("gameVersion")]
    public string? GameVersion { get; set; }

    [JsonPropertyName("updateUrl")]
    public string? UpdateUrl { get; set; }

    [JsonPropertyName("isModPermanent")]
    public bool IsModPermanent { get; set; }

    [JsonPropertyName("excludedFiles")]
    public List<string>? ExcludedFiles { get; set; }
}

/// <summary>扫描到的 mod 条目。</summary>
public sealed class ModEntry
{
    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required bool IsEnabled { get; init; }

    public required long FileSize { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }

    public ModInfo? Info { get; init; }

    /// <summary>解析失败时的原因，用于 UI 提示「这个包坏了」。</summary>
    public string? ParseError { get; init; }

    public string DisplayName => Info?.Name is { Length: > 0 } name
        ? name
        : Path.GetFileNameWithoutExtension(FileName);

    /// <summary>从 updateUrl 里提取 mod 市场 slug（官方 ModManagerPage.cs:254 的逻辑）。</summary>
    public string? MarketSlug
    {
        get
        {
            var url = Info?.UpdateUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            const string marker = "raftmodding.com/api/v1/mods/";
            var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var rest = url[(index + marker.Length)..].Trim('/');
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                rest = rest[..slash];
            }

            return rest.Length > 0 && !rest.Contains("your-mod-slug-here", StringComparison.OrdinalIgnoreCase)
                ? rest
                : null;
        }
    }
}

/// <summary>
/// 读取 .rmod（本质是 ZIP）。
///
/// 官方 <c>ZipModHandler.GetModDataFromZipStream</c> 把条目名做
/// <c>Replace("\\","/")</c> 后只当字典 key，不落盘、不拼路径，因此没有 Zip-Slip。
/// 本实现更保守：只按固定名字取 <c>modinfo.json</c>，完全不信任包内其它文件名，
/// 也不解压任何条目到磁盘。
/// </summary>
public static class RmodReader
{
    private const string ModInfoEntryName = "modinfo.json";

    public static ModInfo ReadModInfo(string rmodPath)
    {
        using var stream = new FileStream(rmodPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), ModInfoEntryName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException("包内缺少 modinfo.json。");
        }

        // 防御 zip bomb：modinfo.json 正常只有几 KB，超过 4MB 一律拒绝。
        if (entry.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException($"modinfo.json 异常过大（{entry.Length} 字节）。");
        }

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);
        var json = reader.ReadToEnd();

        var info = JsonSerializer.Deserialize<ModInfo>(json, JsonOptions);
        if (info is null)
        {
            throw new InvalidDataException("modinfo.json 内容为空。");
        }

        return info;
    }

    /// <summary>读取包内任意一个图片条目（icon/banner），用于 UI 显示。限制大小防止内存爆炸。</summary>
    public static byte[]? TryReadImage(string rmodPath, string? entryName, int maxBytes = 8 * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(rmodPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var normalized = entryName.Replace('\\', '/');
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));

            if (entry is null || entry.Length <= 0 || entry.Length > maxBytes)
            {
                return null;
            }

            using var entryStream = entry.Open();
            using var memory = new MemoryStream((int)entry.Length);
            entryStream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool LooksLikeRmod(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[4];
            return stream.Read(signature) == 4 && signature[0] == 'P' && signature[1] == 'K';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
