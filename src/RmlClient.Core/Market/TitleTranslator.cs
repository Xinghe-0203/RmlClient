using System.Text.Json;
using RmlClient.Core.Configuration;

namespace RmlClient.Core.Market;

/// <summary>
/// 一张「稳定键 → 译名」的翻译表，落盘复用。
///
/// 为什么需要这一层，而不是直接调 <see cref="Translation.TranslationService"/>：
///   1. 列表要一次性显示几十上百个标题，必须批量翻译；
///   2. 标题是搜索的主要目标 —— 用户输入中文时必须能命中译名，
///      所以需要一个「键 → 译名」的映射，而不是一段文本；
///   3. 映射要落盘，否则每次启动都得重翻一遍（免费接口会限流）。
///
/// 市场用它翻 385 个 mod 标题（键 = modId，<c>cache\market-titles.json</c>），
/// 本地模组页用它翻已安装的 mod（键 = slug 或文件名，<c>cache\local-mod-titles.json</c>）。
/// 单条文本的翻译结果另有 <see cref="Translation.TranslationService"/> 的缓存兜底，
/// 所以即使这些表丢了，重翻也基本全是缓存命中。
/// </summary>
public sealed class TitleTranslator
{
    private const int BatchSize = 40;

    private readonly Translation.TranslationService _translation;
    private readonly string _cachePath;
    private readonly object _lock = new();

    /// <summary>目标语言代码 → (键 → 译名)。</summary>
    private Dictionary<string, Dictionary<string, string>> _cache = new();

    private bool _loaded;

    public TitleTranslator(
        RmlPaths paths,
        Translation.TranslationService translation,
        string cacheFileName = "market-titles.json")
    {
        _translation = translation;
        _cachePath = Path.Combine(paths.Cache, cacheFileName);
    }

    /// <summary>当前已翻译好的语言；未翻译过任何标题时为 null。</summary>
    public string? CurrentLanguage { get; private set; }

    /// <summary>确保 <paramref name="mods"/> 的标题都已经翻成 <paramref name="target"/>。</summary>
    public Task EnsureAsync(
        IReadOnlyList<MarketMod> mods,
        Translation.TranslationLanguage target,
        CancellationToken ct = default)
    {
        if (mods.Count == 0)
        {
            return Task.CompletedTask;
        }

        var items = mods
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .Select(m => (Key: m.Id, Text: m.Title))
            .ToList();

        return EnsureAsync((IReadOnlyList<(string Key, string Text)>)items, target, ct);
    }

    /// <summary>
    /// 确保每个 (键, 原文) 都已经翻成 <paramref name="target"/>。
    /// 已翻译过的直接命中本地表，只对缺的部分发请求。
    /// </summary>
    public async Task EnsureAsync(
        IReadOnlyList<(string Key, string Text)> items,
        Translation.TranslationLanguage target,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        EnsureLoaded();

        Dictionary<string, string> table;

        lock (_lock)
        {
            if (!_cache.TryGetValue(target.Code, out table!))
            {
                table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _cache[target.Code] = table;
            }
        }

        List<(string Key, string Text)> missing;

        lock (_lock)
        {
            missing = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Key) &&
                            !string.IsNullOrWhiteSpace(i.Text) &&
                            !table.ContainsKey(i.Key))
                .DistinctBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        CurrentLanguage = target.Code;

        if (missing.Count == 0)
        {
            return;
        }

        // 分批：单个批次失败不影响其它批次。
        for (var offset = 0; offset < missing.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = missing.Skip(offset).Take(BatchSize).ToList();
            var texts = batch.Select(i => i.Text).ToList();

            IReadOnlyList<Translation.TranslationResult> results;

            try
            {
                results = await _translation.TranslateBatchAsync(texts, target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            lock (_lock)
            {
                for (var i = 0; i < batch.Count && i < results.Count; i++)
                {
                    var result = results[i];

                    // 翻译失败就保留原名，列表照样能显示。
                    table[batch[i].Key] = result.Success && !result.Skipped
                        ? result.Text
                        : batch[i].Text;
                }
            }

            Save();
        }
    }

    /// <summary>取译名；没有译名时回退原名。</summary>
    public string GetTitle(MarketMod mod) => GetTitle(mod.Id, mod.Title);

    /// <summary>按键取译名；没有译名时回退 <paramref name="fallback"/>。</summary>
    public string GetTitle(string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }

        lock (_lock)
        {
            if (CurrentLanguage is { } language &&
                _cache.TryGetValue(language, out var table) &&
                table.TryGetValue(key, out var translated) &&
                !string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        return fallback;
    }

    /// <summary>给 <see cref="MarketService.Search"/> 用的映射快照。</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock)
        {
            if (CurrentLanguage is { } language &&
                _cache.TryGetValue(language, out var table))
            {
                return new Dictionary<string, string>(table, StringComparer.OrdinalIgnoreCase);
            }
        }

        return new Dictionary<string, string>();
    }

    /// <summary>清掉某个语言的缓存（标题变了或用户想强制刷新时用）。</summary>
    public void Invalidate(string languageCode)
    {
        lock (_lock)
        {
            _cache.Remove(languageCode);
        }

        Save();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                File.ReadAllText(_cachePath));

            if (loaded is not null)
            {
                _cache = new Dictionary<string, Dictionary<string, string>>(loaded, StringComparer.OrdinalIgnoreCase);

                foreach (var key in _cache.Keys.ToList())
                {
                    _cache[key] = new Dictionary<string, string>(_cache[key], StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception)
        {
            // 缓存坏了就重建，不影响使用。
            _cache = new Dictionary<string, Dictionary<string, string>>();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json;

            lock (_lock)
            {
                json = JsonSerializer.Serialize(_cache);
            }

            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception)
        {
            // 落盘失败只是下次重翻，不影响当前显示。
        }
    }
}
