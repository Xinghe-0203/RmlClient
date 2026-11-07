using System.Text.Json;
using RmlClient.Core.Configuration;
using RmlClient.Core.Diagnostics;

namespace RmlClient.Core.Updates;

public sealed record CoreUpdateStatus(
    string LocalVersion,
    string? RemoteVersion,
    bool UpdateAvailable,
    bool CoreModMissing,
    bool AssetsMissing,
    string? Changelog,
    string? Error)
{
    public bool NeedsInstall => CoreModMissing || AssetsMissing || UpdateAvailable;
}

public sealed record CoreUpdateResult(
    bool Success,
    string Message,
    string? Version = null,
    IReadOnlyList<string>? Details = null);

/// <summary>
/// coremod + rml.assets 的更新器。
///
/// 对齐官方 <c>Launcher\Main.cs:644-680 DownloadModLoaderFiles</c> 的行为，
/// 但修掉三个问题：
///   1. 官方只校验扩展名（<c>.dll</c> / <c>.assets</c>）不校验域名 —— 这里走 UrlGuard
///   2. 官方用 <c>while(!a || !b) await Task.Delay(1)</c> 忙等 —— 这里用 Task.WhenAll
///   3. 官方下载失败也置完成标志（不检查 e.Error）—— 这里失败即抛出
/// </summary>
public sealed class CoreUpdater
{
    private readonly RmlPaths _paths;
    private readonly RmlApi _api;
    private readonly RmlHttpClient _http;

    public CoreUpdater(RmlPaths paths, RmlApi api, RmlHttpClient http)
    {
        _paths = paths;
        _api = api;
        _http = http;
    }

    public async Task<CoreUpdateStatus> CheckAsync(
        string branch,
        string localVersion,
        CancellationToken ct = default)
    {
        try
        {
            var manifest = await _api.GetCoreManifestAsync(branch, ct).ConfigureAwait(false);

            var coreModMissing = !File.Exists(_paths.CoreModDll);
            var assetsMissing = !File.Exists(_paths.AssetsFile);
            var updateAvailable = !string.Equals(localVersion, manifest.Version, StringComparison.Ordinal);

            return new CoreUpdateStatus(
                localVersion,
                manifest.Version,
                updateAvailable,
                coreModMissing,
                assetsMissing,
                manifest.RawChangelog,
                Error: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException
                                       or JsonException)
        {
            return new CoreUpdateStatus(localVersion, null, false,
                !File.Exists(_paths.CoreModDll), !File.Exists(_paths.AssetsFile), null, ex.Message);
        }
    }

    /// <summary>
    /// 下载并校验 coremod 与 assets。
    /// 校验顺序：URL 白名单 → 下载到 .part → SHA512 比对 → 原子替换。
    /// 任一环节失败都不动现有文件。
    /// </summary>
    public async Task<CoreUpdateResult> InstallAsync(
        string branch,
        IProgress<DownloadProgress>? coreModProgress = null,
        IProgress<DownloadProgress>? assetsProgress = null,
        CancellationToken ct = default)
    {
        var details = new List<string>();

        CoreManifest manifest;
        try
        {
            manifest = await _api.GetCoreManifestAsync(branch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException
                                       or JsonException)
        {
            return new CoreUpdateResult(false, $"获取 core 清单失败：{ex.Message}");
        }

        if (!UrlGuard.IsTrustedDownloadUrl(manifest.CoreModUrl))
        {
            return new CoreUpdateResult(false,
                $"coremod 下载地址不在白名单内：{manifest.CoreModUrl}");
        }

        if (!UrlGuard.IsTrustedDownloadUrl(manifest.AssetsUrl))
        {
            return new CoreUpdateResult(false,
                $"assets 下载地址不在白名单内：{manifest.AssetsUrl}");
        }

        var tempCoreMod = Path.Combine(_paths.CacheTemporary, "coremod.dll.new");
        var tempAssets = Path.Combine(_paths.CacheTemporary, "rml.assets.new");

        try
        {
            _paths.EnsureDirectories();

            details.Add($"下载 coremod：{manifest.CoreModUrl}");
            await _http.DownloadFileAsync(manifest.CoreModUrl, tempCoreMod, coreModProgress, ct)
                .ConfigureAwait(false);

            details.Add($"下载 assets：{manifest.AssetsUrl}");
            await _http.DownloadFileAsync(manifest.AssetsUrl, tempAssets, assetsProgress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or InvalidOperationException)
        {
            Cleanup(tempCoreMod, tempAssets);
            return new CoreUpdateResult(false, $"下载失败：{ex.Message}");
        }

        details.Add("校验 coremod 哈希…");
        var coreModHash = await HashUtil.Sha512FileAsync(tempCoreMod, ct).ConfigureAwait(false);
        if (!HashUtil.Matches(manifest.CoreModHash, coreModHash))
        {
            Cleanup(tempCoreMod, tempAssets);
            return new CoreUpdateResult(false,
                "coremod 哈希校验失败，已丢弃下载内容。",
                Details: new[] { $"期望 {manifest.CoreModHash}", $"实际 {coreModHash}" });
        }

        details.Add("校验 assets 哈希…");
        var assetsHash = await HashUtil.Sha512FileAsync(tempAssets, ct).ConfigureAwait(false);
        if (!HashUtil.Matches(manifest.AssetsHash, assetsHash))
        {
            Cleanup(tempCoreMod, tempAssets);
            return new CoreUpdateResult(false,
                "rml.assets 哈希校验失败，已丢弃下载内容。",
                Details: new[] { $"期望 {manifest.AssetsHash}", $"实际 {assetsHash}" });
        }

        try
        {
            _paths.EnsureDirectories();
            ReplaceAtomically(tempCoreMod, _paths.CoreModDll);
            ReplaceAtomically(tempAssets, _paths.AssetsFile);
            details.Add($"已更新到 {manifest.Version}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CoreUpdateResult(false, $"写入失败（游戏或杀软可能正占用文件）：{ex.Message}");
        }

        return new CoreUpdateResult(true, $"core 已更新到 {manifest.Version}。", manifest.Version, details);
    }

    /// <summary>
    /// 用本地文件重装（离线场景）：从 cache 里的备份或用户选择的文件恢复。
    /// </summary>
    public CoreUpdateResult InstallFromLocal(string sourceCoreMod, string sourceAssets)
    {
        try
        {
            if (!File.Exists(sourceCoreMod))
            {
                return new CoreUpdateResult(false, $"找不到 {sourceCoreMod}");
            }

            _paths.EnsureDirectories();
            File.Copy(sourceCoreMod, _paths.CoreModDll, overwrite: true);

            if (File.Exists(sourceAssets))
            {
                File.Copy(sourceAssets, _paths.AssetsFile, overwrite: true);
            }

            return new CoreUpdateResult(true, "已从本地文件安装 core。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CoreUpdateResult(false, $"安装失败：{ex.Message}");
        }
    }

    private static void ReplaceAtomically(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
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
}
