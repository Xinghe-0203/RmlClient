using System.Runtime.InteropServices;

namespace RmlClient.App.Services;

/// <summary>
/// 单实例守卫。
///
/// 客户端会驻留托盘，用户很可能从开始菜单、桌面快捷方式再点一次 ——
/// 若不加锁就会出现多个进程、多个托盘图标、多个窗口抢同一个
/// <c>%APPDATA%\RaftModLoader</c>，互相覆盖 config.ini。
///
/// 做法：命名互斥体判定是否已有实例；第二个实例通过窗口消息
/// 让已有实例把窗口提到前台，然后自己安静退出。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\RmlClient.SingleInstance.09F8DE1C";

    /// <summary>用于唤醒已有实例的自定义窗口消息（WM_APP + 2）。</summary>
    public const int WmShowWindow = 0x8000 + 2;

    private static Mutex? _mutex;
    private static bool _owned;

    /// <summary>
    /// 尝试成为唯一实例。返回 false 表示已有实例在跑，
    /// 此时已经尽力通知它显示窗口，调用方应直接退出。
    /// </summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _owned = createdNew;

            if (createdNew)
            {
                return true;
            }

            // 已存在：叫醒它，然后放弃。
            SignalExistingInstance();
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (Exception)
        {
            // 拿不到互斥体（权限、命名冲突等）时宁可放行，
            // 也不要让用户彻底打不开客户端。
            return true;
        }
    }

    /// <summary>
    /// 让已有实例把主窗口提到前台。
    ///
    /// 用 <c>EnumWindows</c> 找同类窗口（按标题匹配），而不是依赖
    /// 跨进程的窗口句柄传递 —— 后者需要额外的 IPC 通道。
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            var me = Environment.ProcessId;

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

                // 跳过自己（理论上此刻还没窗口，保险起见）。
                if (pid == (uint)me)
                {
                    return true;
                }

                var title = NativeMethods.GetWindowText(hwnd);
                if (string.IsNullOrEmpty(title) ||
                    !title.Contains("Raft Mod Loader", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                NativeMethods.ShowWindow(hwnd, 9 /* SW_RESTORE */);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.PostMessage(hwnd, WmShowWindow, IntPtr.Zero, IntPtr.Zero);
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 叫不醒就算了，至少不要让第二个实例继续跑。
        }
    }

    /// <summary>进程退出时释放互斥体（主要是让下一次启动能立刻拿到）。</summary>
    public static void Release()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            if (_owned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // 不是持有者，忽略。
        }
        finally
        {
            _mutex.Dispose();
            _mutex = null;
            _owned = false;
        }
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        internal static string GetWindowText(IntPtr hwnd)
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new System.Text.StringBuilder(length + 1);
            return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
        }
    }
}
