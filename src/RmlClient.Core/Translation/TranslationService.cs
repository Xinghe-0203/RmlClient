using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RmlClient.Core.Configuration;

namespace RmlClient.Core.Translation;

/// <summary>一次翻译的结果。</summary>
/// <param name="Text">译文；<paramref name="Skipped"/> 为 true 时是原文。</param>
/// <param name="Provider">实际使用的提供方名；跳过或全部失败时为 null。</param>
/// <param name="Skipped">原文已是目标语言，未做翻译。</param>
/// <param name="FromCache">命中本地缓存。</param>
/// <param name="Error">全部提供方都失败时的原因。</param>
public sealed record TranslationResult(
    string Text,
    string? Provider,
    bool Skipped = false,
    bool FromCache = false,
    string? Error = null)
{
    public bool Success => Error is null;
}

/// <summary>
/// 翻译调度：切分长文本、按顺序尝试多个免费提供方、落盘缓存。
///
/// 设计取舍：
///   - **失败绝不阻塞 UI**：任何一个提供方失败都只是换下一个，全失败就返回原文 + 错误原因，
///     详情窗口照常显示原文，不弹错误框打断阅读。
///   - **缓存按「文本 + 目标语言」哈希**，落在 <c>cache\translation.json</c>。
///     mod 描述与更新日志基本不变，第二次打开同一个 mod 是瞬时的，也不会重复消耗免费额度。
///   - **切分优先在换行处断开**，避免把一句话切成两半导致译文语义断裂。
/// </summary>
public sealed class TranslationService
{
    private const string CacheFileName = "translation.json";

    /// <summary>缓存条目上限，超出后按最旧淘汰。防止文件无限增长。</summary>
    private const int MaxCacheEntries = 800;

    private readonly string _cachePath;
    private readonly IReadOnlyList<ITranslationProvider> _providers;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private Dictionary<string, CacheEntry>? _cache;

    public TranslationService(RmlPaths paths, IEnumerable<ITranslationProvider>? providers = null)
    {
        _cachePath = Path.Combine(paths.Cache, CacheFileName);

        _providers = providers?.ToList() is { Count: > 0 } supplied
            ? supplied
            : new List<ITranslationProvider>
            {
                new BingTranslationProvider(),
                new MyMemoryProvider(),
            };
    }

    /// <summary>可用的提供方名字，按尝试顺序。</summary>
    public IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToList();

    /// <summary>
    /// 翻译一段文本到 <paramref name="target"/>。永不抛异常 ——
    /// 失败时返回原文并带上 <see cref="TranslationResult.Error"/>。
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationLanguage target,
        CancellationToken ct = default)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return new TranslationResult(string.Empty, null, Skipped: true);
        }

        // 原文已经是目标语言（例如中文 mod 描述要翻成中文），直接返回，不浪费额度。
        if (LooksLikeTargetLanguage(trimmed, target))
        {
            return new TranslationResult(trimmed, null, Skipped: true);
        }

        var cacheKey = BuildCacheKey(trimmed, target.Code);

        if (await TryGetCachedAsync(cacheKey, ct).ConfigureAwait(false) is { } cached)
        {
            return new TranslationResult(cached.Text, cached.Provider, FromCache: true);
        }

        var failures = new List<string>();

        foreach (var provider in _providers)
        {
            try
            {
                var translated = await TranslateChunkedAsync(provider, trimmed, target, ct)
                    .ConfigureAwait(false);

                await StoreAsync(cacheKey, translated, provider.Name, ct).ConfigureAwait(false);

                return new TranslationResult(translated, provider.Name);
            }
            catch (TranslationException ex)
            {
                failures.Add($"{provider.Name}: {ex.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{provider.Name}: {ex.Message}");
            }
        }

        return new TranslationResult(
            trimmed,
            null,
            Error: failures.Count == 0 ? "没有可用的翻译提供方" : string.Join("；", failures));
    }

    /// <summary>
    /// 批量翻译一批短文本（例如市场里的 385 个 mod 标题）。
    ///
    /// 单个标题只有几十个字符，逐个请求既慢又会很快撞到免费接口的限流，
    /// 所以这里把多条拼成一段（换行分隔）发一次请求，再按行拆回来。
    /// 行数对不上（极少数情况，译文把两行合成一行）就退回逐条翻译。
    ///
    /// 返回结果与入参一一对应，顺序一致。
    /// </summary>
    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationLanguage target,
        CancellationToken ct = default)
    {
        var results = new TranslationResult[texts.Count];
        var pending = new List<int>();

        for (var i = 0; i < texts.Count; i++)
        {
            var trimmed = texts[i]?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                results[i] = new TranslationResult(string.Empty, null, Skipped: true);
                continue;
            }

            if (LooksLikeTargetLanguage(trimmed, target))
            {
                results[i] = new TranslationResult(trimmed, null, Skipped: true);
                continue;
            }

            var cacheKey = BuildCacheKey(trimmed, target.Code);

            if (await TryGetCachedAsync(cacheKey, ct).ConfigureAwait(false) is { } cached)
            {
                results[i] = new TranslationResult(cached.Text, cached.Provider, FromCache: true);
                continue;
            }

            pending.Add(i);
        }

        if (pending.Count == 0)
        {
            return results;
        }

        foreach (var provider in _providers)
        {
            var stillPending = new List<int>();

            foreach (var batch in BuildBatches(texts, pending, provider.MaxChunkLength))
            {
                ct.ThrowIfCancellationRequested();

                if (batch.Count == 1)
                {
                    // 单条就走普通路径（可能还需要切分）。
                    stillPending.AddRange(await TranslateSingleAsync(
                        provider, texts, batch[0], target, results, ct).ConfigureAwait(false));
                    continue;
                }

                try
                {
                    var joined = string.Join("\n", batch.Select(i => texts[i].Trim()));
                    var translated = await provider.TranslateAsync(joined, "auto", target, ct)
                        .ConfigureAwait(false);

                    var lines = translated.TrimEnd().Split('\n');

                    if (lines.Length != batch.Count)
                    {
                        // 行数对不上，不能猜对应关系，退回逐条。
                        stillPending.AddRange(batch);
                        continue;
                    }

                    for (var k = 0; k < batch.Count; k++)
                    {
                        var index = batch[k];
                        var text = lines[k].Trim();

                        results[index] = new TranslationResult(text, provider.Name);

                        await StoreAsync(
                            BuildCacheKey(texts[index].Trim(), target.Code),
                            text,
                            provider.Name,
                            ct).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    stillPending.AddRange(batch);
                }
            }

            pending = stillPending;

            if (pending.Count == 0)
            {
                break;
            }
        }

        // 到这儿还没结果的，就是所有提供方都失败了。
        foreach (var index in pending)
        {
            results[index] = new TranslationResult(texts[index], null, Error: "翻译失败");
        }

        return results;
    }

    private async Task<List<int>> TranslateSingleAsync(
        ITranslationProvider provider,
        IReadOnlyList<string> texts,
        int index,
        TranslationLanguage target,
        TranslationResult[] results,
        CancellationToken ct)
    {
        try
        {
            var translated = await TranslateChunkedAsync(provider, texts[index].Trim(), target, ct)
                .ConfigureAwait(false);

            results[index] = new TranslationResult(translated, provider.Name);

            await StoreAsync(
                BuildCacheKey(texts[index].Trim(), target.Code),
                translated,
                provider.Name,
                ct).ConfigureAwait(false);

            return new List<int>();
        }
        catch (Exception)
        {
            return new List<int> { index };
        }
    }

    /// <summary>
    /// 把待翻项贪心分组，保证每组拼起来不超过提供方上限。
    /// 单条超长的（例如一整段描述）单独成组，交给普通切分路径。
    /// </summary>
    private static List<List<int>> BuildBatches(
        IReadOnlyList<string> texts,
        List<int> pending,
        int maxChunkLength)
    {
        // 留一点余量，译文通常比原文长。
        var limit = (int)(maxChunkLength * 0.9);
        var batches = new List<List<int>>();
        var current = new List<int>();
        var length = 0;

        foreach (var index in pending)
        {
            var text = texts[index].Trim();

            // 含换行的不能拼进批次，否则拆不回来。
            if (text.Length > limit || text.Contains('\n'))
            {
                if (current.Count > 0)
                {
                    batches.Add(current);
                    current = new List<int>();
                    length = 0;
                }

                batches.Add(new List<int> { index });
                continue;
            }

            if (current.Count > 0 && length + text.Length + 1 > limit)
            {
                batches.Add(current);
                current = new List<int>();
                length = 0;
            }

            current.Add(index);
            length += text.Length + 1;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /// <summary>
    /// 把长文本按提供方的上限切成若干段，逐段翻译后按原顺序拼回。
    /// 段与段之间保留原来的换行，让「- 条目」这类列表仍然成行。
    /// </summary>
    private static async Task<string> TranslateChunkedAsync(
        ITranslationProvider provider,
        string text,
        TranslationLanguage target,
        CancellationToken ct)
    {
        var chunks = SplitIntoChunks(text, provider.MaxChunkLength);

        if (chunks.Count == 1)
        {
            return await provider.TranslateAsync(chunks[0], "auto", target, ct).ConfigureAwait(false);
        }

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var piece = await provider.TranslateAsync(chunks[i], "auto", target, ct).ConfigureAwait(false);

            if (i > 0 && builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append('\n');
            }

            builder.Append(piece.TrimEnd());
        }

        return builder.ToString();
    }

    /// <summary>
    /// 按上限切分，尽量在换行或句末断开。
    /// 单行超长（例如一整段没有换行的说明）时退化为按空格断开。
    /// </summary>
    public static List<string> SplitIntoChunks(string text, int maxLength)
    {
        var chunks = new List<string>();

        if (maxLength <= 0)
        {
            maxLength = 400;
        }

        var remaining = text;

        while (remaining.Length > maxLength)
        {
            // 先在窗口内找最后一个换行。
            var cut = remaining.LastIndexOf('\n', maxLength - 1);

            // 退而求其次找句末标点。
            if (cut < maxLength / 3)
            {
                cut = remaining.LastIndexOfAny(new[] { '.', '!', '?', '。', '！', '？' }, maxLength - 1);
            }

            // 再退一步按空格断，避免把单词切开。
            if (cut < maxLength / 3)
            {
                cut = remaining.LastIndexOf(' ', maxLength - 1);
            }

            // 完全没有可断点（例如一长串无空格文本）时硬切。
            if (cut < maxLength / 3)
            {
                cut = maxLength;
            }

            chunks.Add(remaining[..cut].TrimEnd());
            remaining = remaining[cut..].TrimStart('\n', ' ');
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }

        return chunks;
    }

    /// <summary>
    /// 粗略判断文本是否已经是目标语言。
    ///
    /// 只对中文/日文/韩文这类有明显字符集特征的语种做判断；
    /// 拉丁字母语种之间无法可靠区分，一律当作需要翻译。
    /// </summary>
    public static bool LooksLikeTargetLanguage(string text, TranslationLanguage target)
    {
        var sample = text.Length > 400 ? text[..400] : text;
        var letters = sample.Count(char.IsLetter);

        if (letters < 8)
        {
            // 太短（例如只有版本号）不做判断，交给接口。
            return false;
        }

        var cjk = sample.Count(IsCjk);

        return target.Code switch
        {
            "zh-Hans" or "zh-Hant" or "ja" => cjk >= letters * 0.35,
            "ko" => sample.Count(IsHangul) >= letters * 0.35,
            _ => false,
        };
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||   // 中日韩统一表意文字
        (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
        (c >= 0x3040 && c <= 0x30FF);     // 平假名 / 片假名

    private static bool IsHangul(char c) =>
        (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF);

    private static string BuildCacheKey(string text, string targetCode)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(targetCode + "\u0000" + text));
        return Convert.ToHexString(bytes);
    }

    private async Task<CacheEntry?> TryGetCachedAsync(string key, CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();

            if (_cache!.TryGetValue(key, out var entry))
            {
                entry.LastUsed = DateTimeOffset.UtcNow;
                return entry;
            }

            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task StoreAsync(string key, string translated, string provider, CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();

            _cache![key] = new CacheEntry
            {
                Text = translated,
                Provider = provider,
                LastUsed = DateTimeOffset.UtcNow,
            };

            if (_cache.Count > MaxCacheEntries)
            {
                foreach (var stale in _cache
                             .OrderBy(kv => kv.Value.LastUsed)
                             .Take(_cache.Count - MaxCacheEntries)
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    _cache.Remove(stale);
                }
            }

            SaveCache();
        }
        catch (Exception)
        {
            // 缓存写失败不影响本次翻译结果，下次重新翻即可。
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_cache is not null)
        {
            return;
        }

        _cache = new Dictionary<string, CacheEntry>();

        try
        {
            if (File.Exists(_cachePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                    File.ReadAllText(_cachePath));

                if (loaded is not null)
                {
                    _cache = loaded;
                }
            }
        }
        catch (Exception)
        {
            // 缓存损坏就当作空缓存重建。
            _cache = new Dictionary<string, CacheEntry>();
        }
    }

    private void SaveCache()
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception)
        {
            // 同上，缓存只是优化。
        }
    }

    private sealed class CacheEntry
    {
        public string Text { get; set; } = string.Empty;

        public string Provider { get; set; } = string.Empty;

        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }
}
