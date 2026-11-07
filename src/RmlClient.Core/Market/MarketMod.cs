using System.Text.Json;
using System.Text.Json.Serialization;

namespace RmlClient.Core.Market;

/// <summary>
/// raftmodding.com 市场 API 的数据模型。
///
/// 实测结构（<c>GET https://www.raftmodding.com/api/v1/mods</c>，约 917 KB 全量返回）：
/// <code>
/// [{
///   "id": "morestorages",
///   "title": "...",
///   "description": "...",
///   "category": "utility",
///   "author": "...",
///   "bannerImageUrl": "...",
///   "iconImageUrl": "...",
///   "mod-versions": [{ "id", "modId", "version", "changelog", "downloadUrl",
///                      "downloadCount", "createdAt", "updatedAt", ... }]
/// }]
/// </code>
///
/// 注意：该 API 的 <c>page/limit/sort/category/search</c> 参数全部无效，
/// 永远返回同一份全量数据，所以客户端必须自行缓存 + 本地筛选。
/// </summary>
public sealed class MarketMod
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("bannerImageUrl")]
    public string? BannerImageUrl { get; set; }

    [JsonPropertyName("iconImageUrl")]
    public string? IconImageUrl { get; set; }

    [JsonPropertyName("mod-versions")]
    public List<MarketModVersion> Versions { get; set; } = new();

    [JsonIgnore]
    public MarketModVersion? LatestVersion =>
        Versions.OrderByDescending(v => v.CreatedAt ?? DateTimeOffset.MinValue).FirstOrDefault();

    [JsonIgnore]
    public long TotalDownloads => Versions.Sum(v => v.DownloadCount);

    [JsonIgnore]
    public DateTimeOffset? LastUpdated => LatestVersion?.CreatedAt;
}

public sealed class MarketModVersion
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("minRaftVersionId")]
    public int? MinRaftVersionId { get; set; }

    [JsonPropertyName("maxRaftVersionId")]
    public int? MaxRaftVersionId { get; set; }

    [JsonPropertyName("definiteMaxRaftVersion")]
    public bool DefiniteMaxRaftVersion { get; set; }
}

/// <summary>市场数据的磁盘缓存，带时间戳，避免每次启动都拉 917 KB。</summary>
public sealed class MarketCache
{
    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }

    [JsonPropertyName("mods")]
    public List<MarketMod> Mods { get; set; } = new();
}

public static class MarketJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<MarketMod> Parse(string json) =>
        JsonSerializer.Deserialize<List<MarketMod>>(json, Options) ?? new List<MarketMod>();
}
