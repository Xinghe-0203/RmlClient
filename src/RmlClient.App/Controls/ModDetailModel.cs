using RmlClient.Core.Market;
using RmlClient.Core.Mods;

namespace RmlClient.App.Controls;

/// <summary>
/// 详情面板的数据模型：把「市场 API 的 <see cref="MarketMod"/>」与
/// 「本地 .rmod 的 <see cref="ModEntry"/>」归一化成同一份形状。
///
/// 两者信息量差别很大，所以这里不追求完全对齐，只保证详情面板需要的
/// 那几项（标题/副标题/描述/图标/版本列表）一定有值。
/// </summary>
public sealed class ModDetailModel
{
    // 同 ModVersionEntry：不能用 required，XamlTypeInfo 会生成无参构造。
    public string Title { get; init; } = string.Empty;

    public string Subtitle { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>远程图标 URL（市场来源）。</summary>
    public string? IconUrl { get; init; }

    /// <summary>本地 .rmod 路径（本地来源），用于从压缩包里取图标。</summary>
    public string? LocalIconPath { get; init; }

    /// <summary>本地 .rmod 里图标条目的名字，例如 <c>icon.png</c>。</summary>
    public string? LocalIconEntry { get; init; }

    public IReadOnlyList<ModVersionEntry> Versions { get; init; } = Array.Empty<ModVersionEntry>();

    /// <summary>市场来源时带回原对象，供安装/回滚使用。</summary>
    public MarketMod? MarketMod { get; init; }

    /// <summary>本地来源时带回原条目，供卸载/打开目录使用。</summary>
    public ModEntry? LocalMod { get; init; }

    public static ModDetailModel FromMarket(MarketMod mod, string notInstalledText, string? title = null)
    {
        var versions = mod.Versions
            .OrderByDescending(v => v.CreatedAt ?? DateTimeOffset.MinValue)
            .Select(v => new ModVersionEntry
            {
                Key = $"{mod.Id}@{v.Version}",
                Label = v.CreatedAt is { } created
                    ? $"v{v.Version}  ·  {created.LocalDateTime:yyyy-MM-dd}"
                    : $"v{v.Version}",
                Changelog = v.Changelog ?? string.Empty,
                MarketVersion = v,
            })
            .ToList();

        return new ModDetailModel
        {
            Title = string.IsNullOrWhiteSpace(title) ? mod.Title : title,
            Subtitle = $"{mod.Author}  ·  v{mod.LatestVersion?.Version ?? "?"}  ·  {FormatCount(mod.TotalDownloads)} ↓",
            Description = StripMarkup(mod.Description),
            IconUrl = mod.IconImageUrl,
            Versions = versions,
            MarketMod = mod,
        };
    }

    public static ModDetailModel FromLocal(ModEntry entry, string subtitleSuffix, string? title = null)
    {
        var info = entry.Info;

        var changelog = string.Empty;
        var versions = new List<ModVersionEntry>();

        if (!string.IsNullOrWhiteSpace(info?.Version))
        {
            versions.Add(new ModVersionEntry
            {
                Key = $"{entry.FileName}@{info.Version}",
                Label = $"v{info.Version}",
                Changelog = string.Empty,
            });
        }

        return new ModDetailModel
        {
            Title = string.IsNullOrWhiteSpace(title) ? entry.DisplayName : title,
            Subtitle = string.Join("  ·  ", new[]
            {
                info?.Author,
                string.IsNullOrWhiteSpace(info?.Version) ? null : $"v{info.Version}",
                subtitleSuffix,
            }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Description = StripMarkup(info?.Description ?? string.Empty),
            LocalIconPath = entry.FilePath,
            LocalIconEntry = info?.Icon,
            Versions = versions,
            LocalMod = entry,
        };
    }

    /// <summary>
    /// 去掉市场描述里混着的 HTML / BBCode 片段。
    /// 实测 385 个 mod 的描述里没有标签，但作者随时可能加，防一手。
    /// </summary>
    public static string StripMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\[[^\]]+\]", string.Empty);

        return stripped
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Trim();
    }

    private static string FormatCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString(),
    };
}

/// <summary>详情面板里一个可选版本（市场版本或本地 mod 的唯一版本）。</summary>
public sealed class ModVersionEntry
{
    // 这里不能用 required：XAML 编译器会为公开类型生成 XamlTypeInfo，
    // 其中包含无参构造调用，required 成员会让它直接编译失败。
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Changelog { get; init; } = string.Empty;

    public MarketModVersion? MarketVersion { get; init; }
}
