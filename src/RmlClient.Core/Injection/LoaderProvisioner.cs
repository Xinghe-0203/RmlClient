using System.IO.Compression;
using System.Reflection;
using RmlClient.Core.Configuration;
using RmlClient.Core.Diagnostics;

namespace RmlClient.Core.Injection;

/// <summary>
/// 负责把内置的 AssemblyLoader 释放到 <c>%APPDATA%\RaftModLoader\binaries\HMLCoreLibrary.dll</c>。
///
/// 为什么需要它：官方启动器在安装阶段会把自身资源里的
/// <c>Resources.AssemblyLoader.dll</c> 写到 <c>binaries\HMLCoreLibrary.dll</c>
/// （见 <c>Launcher\Main.cs:798</c> 的 <c>File.WriteAllBytes(FILES_ASSEMBLYLOADER, ...)</c>）。
/// 本客户端只接管了 coremod/assets 的下载，没有这一步，于是「只装了本客户端、
/// 从没跑过官方启动器」的机器上注入必然失败，报「缺少 AssemblyLoader.dll，请先更新 core。」
///
/// 处理策略：
///   - 目标文件已存在且大小正确 → 直接复用，不动它（用户可能用的是更新版本的官方加载器）
///   - 缺失或明显损坏 → 从内嵌的 gzip 资源解出来，先写 <c>.tmp</c> 再原子替换
///   - 解压后按已知 SHA256 校验，校验不过则删掉刚写的文件并返回失败（避免留下半个坏 DLL）
/// </summary>
public sealed class LoaderProvisioner
{
    /// <summary>内嵌资源的逻辑名（与 csproj 里的 LogicalName 一致）。</summary>
    private const string ResourceName = "RmlClient.Core.HMLCoreLibrary.dll.gz";

    /// <summary>官方 2.8.10 的 AssemblyLoader 大小，用来判断现有文件是否完整。</summary>
    public const long ExpectedLength = 22851072;

    /// <summary>官方 2.8.10 的 AssemblyLoader SHA256，用来校验解出来的内容。</summary>
    public const string ExpectedSha256 =
        "4A2880F33F5544C835DA6F30FA91873BBC317026CD85297F6D67728FD7EC8B99";

    private readonly RmlPaths _paths;

    public LoaderProvisioner(RmlPaths paths) => _paths = paths;

    /// <summary>内置加载器是否可用（资源存在）。发布包缺资源时用来降级提示。</summary>
    public static bool HasEmbeddedLoader =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>
    /// 确保 <c>binaries\HMLCoreLibrary.dll</c> 存在。返回结果里的 <c>Changed</c> 表示这次是否真的写了盘。
    /// 不抛异常：失败时把原因放在 <c>Message</c> 里，由调用方决定怎么提示。
    /// </summary>
    public LoaderProvisionResult Ensure()
    {
        var target = _paths.AssemblyLoaderDll;

        if (File.Exists(target))
        {
            var length = new FileInfo(target).Length;
            if (length == ExpectedLength)
            {
                return new LoaderProvisionResult(true, false, "加载器已存在。", target);
            }

            // 大小不对：可能是拷了一半，也可能是用户装了别的版本。
            // 只有比内置的小才当作损坏覆盖，比内置的大就保留（更新的官方版本）。
            if (length > ExpectedLength)
            {
                return new LoaderProvisionResult(true, false, "已存在更新的加载器，保留原文件。", target);
            }
        }

        return Extract(target);
    }

    /// <summary>
    /// 把内置加载器解压到任意路径（doorstop 模式需要把它写成游戏目录下的
    /// <c>RaftModLoader.dll</c>，所以不能只写死 binaries 那一处）。
    /// </summary>
    public LoaderProvisionResult Extract(string destinationPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                return new LoaderProvisionResult(false, false,
                    "程序内没有内置加载器，请先运行一次官方启动器或手动放入 HMLCoreLibrary.dll。",
                    destinationPath);
            }

            var tempPath = destinationPath + ".tmp";
            long written;

            using (var gzip = new GZipStream(resource, CompressionMode.Decompress, leaveOpen: false))
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                gzip.CopyTo(output);
                written = output.Length;
            }

            if (written != ExpectedLength)
            {
                TryDelete(tempPath);
                return new LoaderProvisionResult(false, false,
                    $"内置加载器解压后大小异常（{written} 字节，期望 {ExpectedLength} 字节）。", destinationPath);
            }

            var hash = HashUtil.Sha256File(tempPath);
            if (!HashUtil.Matches(ExpectedSha256, hash))
            {
                TryDelete(tempPath);
                return new LoaderProvisionResult(false, false, $"内置加载器校验失败（{hash}）。", destinationPath);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return new LoaderProvisionResult(true, true, "已释放内置加载器。", destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(destinationPath + ".tmp");
            return new LoaderProvisionResult(false, false, $"释放加载器失败：{ex.Message}", destinationPath);
        }
    }

    /// <summary>读出内嵌的 doorstop winhttp.dll（失败返回 null）。</summary>
    public static byte[]? ReadEmbeddedWinHttp() => ReadResource("RmlClient.Core.winhttp.dll");

    /// <summary>读出内嵌的 doorstop_config.ini 模板（失败返回 null）。</summary>
    public static string? ReadEmbeddedDoorstopConfig()
    {
        var bytes = ReadResource("RmlClient.Core.doorstop_config.ini");
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
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
            // 删不掉就算了，下次会重写。
        }
    }
}

/// <summary><see cref="LoaderProvisioner"/> 的结果。</summary>
public sealed record LoaderProvisionResult(bool Success, bool Changed, string Message, string Path);
