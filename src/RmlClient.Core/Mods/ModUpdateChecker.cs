using System.Net.Http;
using RmlClient.Core.Diagnostics;
using RmlClient.Core.Logging;
using RmlClient.Core.Updates;

namespace RmlClient.Core.Mods;

/// <summary>单个 mod 的更新检查结果。</summary>
public sealed record ModUpdateResult(
    ModEntry Mod,
    string? LatestVersion,
    bool UpdateAvailable,
    string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 检查已安装 mod 是否有新版本。
///
/// 官方做法（<c>HUtils.GetModVersion</c>）就是 GET <c>modinfo.json</c> 里的
/// <c>updateUrl</c> 并把响应体当版本号文本，本实现沿用同一约定：
///   - 正常返回纯文本版本号，可能带 <c>v</c> 前缀（实测 <c>3.2</c> / <c>v1.3.0</c> 都有）
///   - mod 被下架或没有 version.txt 时返回 JSON 错误体，需识别后当作「查不到」而非「有新版本」
///
/// 与官方的区别：官方用被禁用了证书校验的 <c>HttpCertHandler</c>，这里走正常 TLS。
/// </summary>
public sealed class ModUpdateChecker
{
    private readonly HttpClient _http;
    private readonly ClientLogger? _logger;

    public ModUpdateChecker(HttpClient? http = null, ClientLogger? logger = null)
    {
        _http = http ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _logger = logger;
    }

    /// <summary>检查一批 mod；没有 updateUrl 的会被跳过（不产生结果）。</summary>
    public async Task<IReadOnlyList<ModUpdateResult>> CheckAsync(
        IReadOnlyList<ModEntry> mods,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        // 只查能查的：有 updateUrl、且本地版本号可解析。
        var targets = mods
            .Where(m => m.Info is not null && !string.IsNullOrWhiteSpace(m.Info.UpdateUrl))
            .ToList();

        var results = new List<ModUpdateResult>(targets.Count);
        var done = 0;

        foreach (var mod in targets)
        {
            ct.ThrowIfCancellationRequested();

            results.Add(await CheckOneAsync(mod, ct));
            progress?.Report(++done);

            // 官方站点对密集请求不友好，顺序查 + 小间隔，避免触发限流。
            if (done < targets.Count)
            {
                try
                {
                    await Task.Delay(250, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return results;
    }

    private async Task<ModUpdateResult> CheckOneAsync(ModEntry mod, CancellationToken ct)
    {
        var localVersion = mod.Info?.Version;
        var url = mod.Info?.UpdateUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ModUpdateResult(mod, null, false, null);
        }

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 通常意味着 mod 已从市场下架，这不算「检查失败」到要报错的程度。
                var message = (int)response.StatusCode == 404
                    ? "该 mod 已不在市场中"
                    : $"HTTP {(int)response.StatusCode}";

                return new ModUpdateResult(mod, null, false, message);
            }

            var text = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();

            // version.txt 正常只返回几十字节；返回 JSON 说明是错误体。
            if (text.Length == 0)
            {
                return new ModUpdateResult(mod, null, false, "服务器返回空内容");
            }

            if (text.StartsWith('{') || text.StartsWith('['))
            {
                return new ModUpdateResult(mod, null, false, "该 mod 已不在市场中");
            }

            // 防御异常长的响应（正常版本号不会超过几十字符）。
            if (text.Length > 64)
            {
                return new ModUpdateResult(mod, null, false, "返回内容不是版本号");
            }

            var latest = text.TrimStart('v', 'V');
            var available = IsNewer(latest, localVersion);

            return new ModUpdateResult(mod, latest, available, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn("ModUpdate", $"检查 {mod.DisplayName} 更新失败：{ex.Message}");
            return new ModUpdateResult(mod, null, false, ex.Message);
        }
    }

    /// <summary>
    /// 判断 <paramref name="candidate"/> 是否比 <paramref name="current"/> 新。
    /// 版本号格式不统一（<c>3.2</c> / <c>1.4.4</c> / <c>1.2.0-beta</c> 都有），
    /// 解析失败时保守返回 false，避免误报「有更新」。
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        if (!ClientVersion.TryParse(candidate, out var a) || !ClientVersion.TryParse(current, out var b))
        {
            // 解析不了就认为没有更新：宁可漏报，也不要误报让用户白下一次。
            return false;
        }

        return a > b;
    }
}
