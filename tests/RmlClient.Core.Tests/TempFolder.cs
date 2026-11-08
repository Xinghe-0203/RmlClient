namespace RmlClient.Core.Tests;

/// <summary>
/// 一次性的临时目录，测试结束自动删除。
///
/// 所有测试都只在自己的临时目录里读写，绝不碰真实的
/// <c>%APPDATA%\RaftModLoader</c> 与游戏安装目录。
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "rmlclient-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    /// <summary>在临时目录下建文件（自动建父目录），返回完整路径。</summary>
    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不该让用例失败。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
