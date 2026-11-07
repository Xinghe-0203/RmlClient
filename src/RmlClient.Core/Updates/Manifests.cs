using System.Text.Json.Serialization;

namespace RmlClient.Core.Updates;

/// <summary>
/// <c>/data/launcher.php?branch=xxx</c> 的响应。
///
/// 官方用 <c>DataContractJsonSerializer</c> 反序列化到
/// <c>ModLoaderVersion { version, url, hash, rawChangelog, fullChangelogUrl }</c>。
/// 实测响应还包含 <c>branch</c> 与 <c>error</c> 字段，本类一并建模。
/// </summary>
public sealed class LauncherManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("rawChangelog")]
    public string RawChangelog { get; set; } = string.Empty;

    [JsonPropertyName("fullChangelogUrl")]
    public string FullChangelogUrl { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// <c>/data/client.php?branch=xxx</c> 的响应：coremod + assets。
/// </summary>
public sealed class CoreManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("coremod_url")]
    public string CoreModUrl { get; set; } = string.Empty;

    [JsonPropertyName("assets_url")]
    public string AssetsUrl { get; set; } = string.Empty;

    [JsonPropertyName("coremod_hash")]
    public string CoreModHash { get; set; } = string.Empty;

    [JsonPropertyName("assets_hash")]
    public string AssetsHash { get; set; } = string.Empty;

    [JsonPropertyName("rawChangelog")]
    public string RawChangelog { get; set; } = string.Empty;

    [JsonPropertyName("fullChangelogUrl")]
    public string FullChangelogUrl { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}
