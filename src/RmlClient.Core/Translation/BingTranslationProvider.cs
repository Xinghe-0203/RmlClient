using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RmlClient.Core.Translation;

/// <summary>
/// 走 Bing 网页版翻译的免费接口（<c>www.bing.com/ttranslatev3</c>）。
///
/// 为什么选它：
///   - 国内可直连（Google 系接口连不上，实测 <c>translate.googleapis.com</c> 超时）
///   - 免费、无需 API Key、不需要注册
///   - 单次约 1000 字符，足够覆盖 mod 描述（最长 255）与绝大多数更新日志
///   - 译文质量明显好于 MyMemory（实测 5417 字符的 changelog 译文可读）
///
/// 用法上要注意三点（都是实测出来的）：
///   1. 必须先 GET 一次 <c>/translator</c> 拿 cookie 与页面里的
///      <c>IG</c> / <c>IID</c> / <c>params_AbusePreventionHelper</c>，否则直接 POST 返回 400。
///   2. <c>fromLang</c> 自动检测必须写 <c>auto-detect</c>；写 <c>auto</c> 或留空都是 400。
///   3. token 有有效期，过期后接口返回 400 —— 这里检测到 400 就重新握手一次再重试。
///
/// 这是非官方接口，随时可能改。所以调用方应当把它当成「可能失败」的一路，
/// 失败时回退到 <see cref="MyMemoryProvider"/>。
/// </summary>
public sealed partial class BingTranslationProvider : ITranslationProvider
{
    private const string HomeUrl = "https://www.bing.com/translator";

    /// <summary>实测 900 字符可用、1200 字符返回 400，这里留出余量。</summary>
    public const int ChunkLength = 800;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _handshakeLock = new(1, 1);

    private string? _ig;
    private string? _iid;
    private string? _key;
    private string? _token;
    private DateTimeOffset _handshakeAt;

    public BingTranslationProvider(HttpClient? http = null)
    {
        _http = http ?? CreateDefaultClient();
    }

    public string Name => "Bing";

    public int MaxChunkLength => ChunkLength;

    /// <summary>token 保守按 5 分钟有效处理，过期就重新握手。</summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public async Task<string> TranslateAsync(
        string text,
        string from,
        TranslationLanguage target,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // 一次重试：token 过期或会话失效时重新握手。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureHandshakeAsync(ct).ConfigureAwait(false);

            var body = string.Join("&",
                $"fromLang={Uri.EscapeDataString(NormalizeSource(from))}",
                $"text={Uri.EscapeDataString(text)}",
                $"to={Uri.EscapeDataString(target.BingCode)}",
                $"token={Uri.EscapeDataString(_token!)}",
                $"key={Uri.EscapeDataString(_key!)}");

            var url = $"https://www.bing.com/ttranslatev3?isVertical=1&IG={Uri.EscapeDataString(_ig!)}" +
                      $"&IID={Uri.EscapeDataString(_iid!)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            request.Headers.TryAddWithoutValidation("Referer", HomeUrl);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.bing.com");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new TranslationException(
                    TranslationFailure.Network, $"连接 Bing 翻译失败：{ex.Message}", ex);
            }

            using (response)
            {
                var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // 400 通常意味着 token 过期或参数被拒，重新握手后再来一次。
                if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0)
                {
                    InvalidateHandshake();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TranslationException(
                        TranslationFailure.Rejected,
                        $"Bing 翻译返回 HTTP {(int)response.StatusCode}：{Truncate(payload, 120)}");
                }

                return ParseTranslation(payload);
            }
        }

        throw new TranslationException(TranslationFailure.Rejected, "Bing 翻译多次握手后仍被拒绝");
    }

    /// <summary>从响应里取出译文。结构：<c>[{"translations":[{"text":"..."}]}]</c>。</summary>
    private static string ParseTranslation(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                throw new TranslationException(
                    TranslationFailure.Rejected, $"Bing 返回了非预期结构：{Truncate(payload, 120)}");
            }

            var first = doc.RootElement[0];
            if (!first.TryGetProperty("translations", out var translations) ||
                translations.ValueKind != JsonValueKind.Array ||
                translations.GetArrayLength() == 0)
            {
                throw new TranslationException(
                    TranslationFailure.Rejected, $"Bing 响应缺少 translations：{Truncate(payload, 120)}");
            }

            var text = translations[0].TryGetProperty("text", out var t) ? t.GetString() : null;

            if (string.IsNullOrEmpty(text))
            {
                throw new TranslationException(TranslationFailure.Rejected, "Bing 返回了空译文");
            }

            return text;
        }
        catch (JsonException ex)
        {
            throw new TranslationException(
                TranslationFailure.Rejected, $"Bing 响应不是合法 JSON：{Truncate(payload, 120)}", ex);
        }
    }

    /// <summary>Bing 只认 <c>auto-detect</c> 这一种自动检测写法。</summary>
    private static string NormalizeSource(string from) =>
        string.IsNullOrWhiteSpace(from) || from.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "auto-detect"
            : from;

    private async Task EnsureHandshakeAsync(CancellationToken ct)
    {
        if (IsHandshakeFresh())
        {
            return;
        }

        await _handshakeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsHandshakeFresh())
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, HomeUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation(
                "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException(
                    TranslationFailure.Network,
                    $"打开 Bing 翻译页面失败：HTTP {(int)response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var ig = IgPattern().Match(html);
            var iid = IidPattern().Match(html);
            var helper = HelperPattern().Match(html);

            if (!ig.Success || !iid.Success || !helper.Success)
            {
                throw new TranslationException(
                    TranslationFailure.Rejected,
                    "Bing 翻译页面结构变化，无法取得 IG/IID/token（接口可能已改动）");
            }

            _ig = ig.Groups[1].Value;
            _iid = iid.Groups[1].Value;
            _key = helper.Groups[1].Value;
            _token = helper.Groups[2].Value;
            _handshakeAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _handshakeLock.Release();
        }
    }

    private bool IsHandshakeFresh() =>
        _ig is not null && _iid is not null && _key is not null && _token is not null &&
        DateTimeOffset.UtcNow - _handshakeAt < TokenLifetime;

    private void InvalidateHandshake() => _handshakeAt = DateTimeOffset.MinValue;

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    [GeneratedRegex("IG:\"([^\"]+)\"")]
    private static partial Regex IgPattern();

    [GeneratedRegex("data-iid=\"([^\"]+)\"")]
    private static partial Regex IidPattern();

    [GeneratedRegex("params_AbusePreventionHelper\\s*=\\s*\\[(\\d+),\\s*\"([^\"]+)\"")]
    private static partial Regex HelperPattern();
}
