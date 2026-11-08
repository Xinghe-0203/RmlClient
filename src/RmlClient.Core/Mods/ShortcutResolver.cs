using System.Runtime.InteropServices;

namespace RmlClient.Core.Mods;

/// <summary>
/// 解析 <c>.lnk</c> 快捷方式指向的文件夹。
///
/// 官方 coremod 除了 <c>*.rmod</c> 也加载 <c>*.lnk</c>（
/// <c>ModManagerPage.cs:121</c>：<c>x.Name.ToLower().EndsWith(".rmod") || x.Name.EndsWith(".lnk")</c>），
/// 走 <c>FolderModHandler</c>：用 <c>Shortcut.ReadFromFile(...).LinkTargetIDList.Path</c>
/// 取目标目录，再从目录里读 <c>modinfo.json</c>。这是 mod 作者开发调试时用的方式 ——
/// 改代码不用每次重新打包。
///
/// 这里用 <c>WScript.Shell</c> 的 <c>CreateShortcut</c> 拿 <c>TargetPath</c>，
/// 避免自己实现 Shell Link 二进制格式（IDList 解析既繁琐又容易在
/// 不同 Windows 版本上出偏差）。
/// </summary>
public static class ShortcutResolver
{
    /// <summary>取快捷方式指向的目标路径。失败返回 null（不抛异常）。</summary>
    public static string? ResolveTarget(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            dynamic dynShell = shell;
            shortcut = dynShell.CreateShortcut(lnkPath);

            dynamic dynShortcut = shortcut;
            string? target = dynShortcut.TargetPath;

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.ReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }
}
