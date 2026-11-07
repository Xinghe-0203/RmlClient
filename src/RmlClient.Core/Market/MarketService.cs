using System.Text.Json;
using RmlClient.Core.Configuration;
using RmlClient.Core.Diagnostics;
using RmlClient.Core.Mods;
using RmlClient.Core.Updates;

namespace RmlClient.Core.Market;

/// <summary>
/// mod 市场服务：拉取全量列表、本地缓存、搜索筛选、一键安装、版本回滚。
///
/// 因为 API 不支持分页/搜索，这里把 917 KB 的响应缓存在
/// <c>%APPDATA%\RaftModLoader\cache\market.json</c>，
/// 之后所有筛选都在内存里做（毫秒级，远快于每次请求）。
/// </summary>
public sealed class MarketService
{
    private readonly RmlPaths _paths;
    private readonly RmlHttpClient _http;
    private readonly RmlApi _api;
    private readonly ModRepository _mods;

    private List<MarketMod> _cached = new();
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public MarketService(RmlPaths paths, RmlHttpClient http, RmlApi api, ModRepository mods)
    {
        _paths = paths;
        _http = http;
        _api = api;
        _mods = mods;
    }

    public IReadOnlyList<MarketMod> Mods => _cached;

    public DateTimeOffset LastFetchedAt => _cachedAt;

    public bool HasData => _cached.Count > 0;

    /// <summary>从磁盘缓存加载，不联网。</summary>
    public bool LoadFromDisk()
    {
        if (!File.Exists(_paths.MarketCacheFile))
        {
            return false;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<MarketCache>(
                File.ReadAllText(_paths.MarketCacheFile), MarketJson.Options);

            if (cache is null || cache.Mods.Count == 0)
            {
                return false;
            }

            _cached = cache.Mods;
            _cachedAt = cache.FetchedAt;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>联网刷新全量列表并写盘。失败时保留旧缓存。</summary>
    public async Task<int> RefreshAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        status?.Report("正在获取 mod 列表…");

        var json = await _http.GetStringAsync(RmlApi.ModListUrl(), TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);

        status?.Report("正在解析…");
        var mods = MarketJson.Parse(json);

        if (mods.Count == 0)
        {
            throw new InvalidDataException("市场返回了空列表。");
        }

        _cached = mods;
        _cachedAt = DateTimeOffset.UtcNow;

        try
        {
            _paths.EnsureDirectories();
            var cache = new MarketCache { FetchedAt = _cachedAt, Mods = mods };
            var temp = _paths.MarketCacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, MarketJson.Options));
            File.Move(temp, _paths.MarketCacheFile, overwrite: true);
        }
        catch (IOException)
        {
            // 缓存写失败不影响本次使用。
        }

        status?.Report($"已加载 {mods.Count} 个 mod。");
        return mods.Count;
    }

    public IReadOnlyList<MarketMod> Search(
        string? query = null,
        string? category = null,
        MarketSort sort = MarketSort.Popular,
        IReadOnlyDictionary<string, string>? titleAliases = null)
    {
        IEnumerable<MarketMod> queryable = _cached;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            queryable = queryable.Where(m => terms.All(t =>
                Contains(m.Id, t) ||
                Contains(m.Title, t) ||
                Contains(m.Description, t) ||
                Contains(m.Author, t) ||
                // 译名也参与匹配：用户输入中文时能直接搜到英文标题的 mod。
                Contains(LookupAlias(titleAliases, m.Id), t)));
        }

        if (!string.IsNullOrWhiteSpace(category) &&
            !category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            queryable = queryable.Where(m =>
                m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        queryable = sort switch
        {
            MarketSort.Popular => queryable.OrderByDescending(m => m.TotalDownloads),
            MarketSort.Recent => queryable.OrderByDescending(m => m.LastUpdated ?? DateTimeOffset.MinValue),
            MarketSort.Name => queryable.OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => queryable,
        };

        return queryable.ToList();

        static bool Contains(string? haystack, string needle) =>
            haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

        static string? LookupAlias(IReadOnlyDictionary<string, string>? aliases, string id) =>
            aliases is not null && aliases.TryGetValue(id, out var alias) ? alias : null;
    }

    public IReadOnlyList<string> Categories =>
        _cached.Select(m => m.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 下载并安装指定版本。校验顺序：
    /// URL 白名单 → 下载 → 读包验证 modinfo.json → 移入 mods 目录。
    /// </summary>
    public async Task<ModOperationResult> InstallAsync(
        MarketMod mod,
        MarketModVersion? version = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var target = version ?? mod.LatestVersion;
        if (target is null)
        {
            return ModOperationResult.Fail("这个 mod 没有任何可下载版本。");
        }

        if (string.IsNullOrWhiteSpace(target.DownloadUrl))
        {
            return ModOperationResult.Fail("该版本缺少 downloadUrl。");
        }

        var url = UrlGuard.ResolveModDownloadUrl(target.DownloadUrl);

        // 官网对 .rmod 下载默认会先跳病毒扫描确认页（HTTP 300），
        // 带上这个参数才是直链 —— 官方启动器也是这么拼的。
        url = AppendQuery(url, "ignoreVirusScan=true");

        if (!UrlGuard.IsTrustedDownloadUrl(url))
        {
            return ModOperationResult.Fail($"下载地址不在白名单内：{url}");
        }

        _paths.EnsureDirectories();
        var temp = Path.Combine(_paths.CacheTemporary, $"market.{mod.Id}.{target.Version}.rmod");

        try
        {
            await _http.DownloadFileAsync(url, temp, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or InvalidOperationException)
        {
            TryDelete(temp);
            return ModOperationResult.Fail($"下载失败：{ex.Message}");
        }

        // 用官方命名落盘：modinstaller.<slug>.rmod。
        // coremod 只按扩展名扫描，但保持同名约定能让「已安装」状态、
        // 卸载残留和官方启动器互相识别。
        var targetFileName = $"modinstaller.{mod.Id}{RmlPaths.ModExtension}";
        var result = _mods.Install(temp, targetFileName);
        TryDelete(temp);

        if (result.Success)
        {
            return ModOperationResult.Ok($"已安装 {mod.Title} {target.Version}。");
        }

        return result;
    }

    /// <summary>
    /// 按 slug 在市场缓存里找 mod。
    /// modinfo.json 的 updateUrl 形如 <c>.../api/v1/mods/&lt;slug&gt;/version.txt</c>，
    /// 而市场 id 就是去掉连字符的 slug（<c>more-storages</c> → <c>morestorages</c>），
    /// 所以两种写法都要能命中。
    /// </summary>
    public MarketMod? FindBySlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || _cached.Count == 0)
        {
            return null;
        }

        var normalized = slug.Replace("-", string.Empty);

        foreach (var mod in _cached)
        {
            if (string.Equals(mod.Id, slug, StringComparison.OrdinalIgnoreCase))
            {
                return mod;
            }
        }

        foreach (var mod in _cached)
        {
            if (string.Equals(mod.Id.Replace("-", string.Empty), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return mod;
            }
        }

        return null;
    }

    /// <summary>判断某个市场 mod 是否已装、本地版本是多少。</summary>
    public (bool Installed, string? LocalVersion, bool Enabled) GetLocalState(
        MarketMod mod,
        IReadOnlyList<ModEntry> localMods)
    {        foreach (var local in localMods)
        {
            var matchesId = local.MarketSlug?.Equals(mod.Id, StringComparison.OrdinalIgnoreCase) == true;

            // 文件名可能是 itemspawner.rmod，也可能是官方风格的
            // modinstaller.itemspawner.rmod，两种都要认。
            var name = local.FileName;
            if (name.StartsWith("modinstaller.", StringComparison.OrdinalIgnoreCase))
            {
                name = name["modinstaller.".Length..];
            }

            var matchesFile = name.StartsWith(mod.Id + ".", StringComparison.OrdinalIgnoreCase)
                              || name.Equals(mod.Id + RmlPaths.ModExtension, StringComparison.OrdinalIgnoreCase)
                              || name.Equals(mod.Id + RmlPaths.DisabledModExtension, StringComparison.OrdinalIgnoreCase);

            if (matchesId || matchesFile)
            {
                return (true, local.Info?.Version, local.IsEnabled);
            }
        }

        return (false, null, false);
    }

    private static string AppendQuery(string url, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return url;
        }

        var separator = url.Contains('?') ? "&" : "?";
        return url + separator + query;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}

public enum MarketSort
{
    Popular,
    Recent,
    Name,
}
