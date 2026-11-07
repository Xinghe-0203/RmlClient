using System.Net;
using System.Text.Json;

namespace RmlClient.Core.Translation;

/// <summary>
/// MyMemory（<c>api.mymemory.translated.net</c>）免费翻译接口，作为 Bing 的备用通道。
///
/// 特点：
///   - 免费、无需 Key，国内可直连
///   - 单次上限 **500 字符**（实测 660 字符返回 403
///     <c>QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500 CHARS</c>），
///     所以 <see cref="MaxChunkLength"/> 取 450 留余量
///   - 匿名每天约 5000 字符额度；带上 <c>de=邮箱</c> 可提到 50000 字符
///   - 译文质量一般，但对 mod 描述这种短句够用
///
/// 这里带上一个固定的联系邮箱，符合 MyMemory 的用法要求（他们靠这个识别滥用）。
/// </summary>
public sealed class MyMemoryProvider : ITranslationProvider
{
    /// <summary>实测上限 500 字符，取 450 留出转义与参数余量。</summary>
    public const int ChunkLength = 450;

    /// <summary>MyMemory 要求带上联系方式以便提高配额，这不是密钥。</summary>
    private const string ContactEmail = "rmlclient@outlook.com";

    private readonly HttpClient _http;

    public MyMemoryProvider(HttpClient? http = null)
    {
        _http = http ?? CreateDefaultClient();
    }

    public string Name => "MyMemory";

    public int MaxChunkLength => ChunkLength;

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

        var source = string.IsNullOrWhiteSpace(from) || from.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : from;

        var url = "https://api.mymemory.translated.net/get" +
                  $"?q={Uri.EscapeDataString(text)}" +
                  $"&langpair={Uri.EscapeDataString(source)}|{Uri.EscapeDataString(target.MyMemoryCode)}" +
                  $"&de={Uri.EscapeDataString(ContactEmail)}";

        string payload;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException(
                    TranslationFailure.Rejected,
                    $"MyMemory 返回 HTTP {(int)response.StatusCode}：{Truncate(payload, 120)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new TranslationException(
                TranslationFailure.Network, $"连接 MyMemory 失败：{ex.Message}", ex);
        }

        return ParseTranslation(payload);
    }

    private static string ParseTranslation(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // responseStatus 可能是数字也可能是字符串，两种都处理。
            var status = root.TryGetProperty("responseStatus", out var s)
                ? (s.ValueKind == JsonValueKind.Number ? s.GetInt32() : int.TryParse(s.GetString(), out var n) ? n : 0)
                : 0;

            if (!root.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var translated))
            {
                throw new TranslationException(
                    TranslationFailure.Rejected, $"MyMemory 响应缺少 translatedText：{Truncate(payload, 120)}");
            }

            var text = translated.GetString() ?? string.Empty;

            // MyMemory 在超限/限流时把错误信息塞进 translatedText，HTTP 状态仍是 200。
            if (status != 200 || text.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                throw new TranslationException(
                    TranslationFailure.TooLong, $"MyMemory 拒绝了请求（status={status}）：{Truncate(text, 120)}");
            }

            if (text.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            {
                throw new TranslationException(
                    TranslationFailure.Rejected, $"MyMemory 配额已用尽：{Truncate(text, 120)}");
            }

            return text;
        }
        catch (JsonException ex)
        {
            throw new TranslationException(
                TranslationFailure.Rejected, $"MyMemory 响应不是合法 JSON：{Truncate(payload, 120)}", ex);
        }
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
