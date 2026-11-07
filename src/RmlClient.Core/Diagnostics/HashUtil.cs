using System.Security.Cryptography;
using System.Text;

namespace RmlClient.Core.Diagnostics;

/// <summary>
/// 哈希工具。
///
/// 官方有两套不一致的实现，本项目统一为一个大写十六进制版本：
///   - <c>Main.CalculateSHA512</c>：<c>BitConverter.ToString(...).Replace("-","").ToUpperInvariant()</c>（大写）
///   - <c>HUtils.GetStringSHA512Hash</c>：<c>Convert.ToString(b,16).PadLeft(2,'0')</c>（小写）
/// 服务器下发的 hash 全是大写，所以统一用大写比较，比较时忽略大小写以防万一。
/// </summary>
public static class HashUtil
{
    public static string ToHexUpper(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", string.Empty).ToUpperInvariant();

    public static string Sha512File(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.SequentialScan);

        using var sha = SHA512.Create();
        return ToHexUpper(sha.ComputeHash(stream));
    }

    public static async Task<string> Sha512FileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA512.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return ToHexUpper(hash);
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        return ToHexUpper(sha.ComputeHash(stream));
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return ToHexUpper(hash);
    }

    public static string Sha512String(string value)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(value));
        return ToHexUpper(bytes);
    }

    /// <summary>
    /// 服务器下发的 hash 与本地计算结果比较。忽略大小写与两侧空白。
    /// </summary>
    public static bool Matches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
