using System.Runtime.InteropServices;

namespace RmlClient.App.Services;

/// <summary>
/// 系统托盘图标。WinUI 3 没有自带托盘 API，这里直接用 Shell_NotifyIcon，
/// 并在一个独立线程上跑消息循环承载图标（主窗口线程被 XAML 占用）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const uint MF_STRING = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int IDM_OPEN = 1;
    private const int IDM_EXIT = 2;

    private readonly string _iconPath;
    private string _tooltip;
    private readonly string _openText;
    private readonly string _exitText;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _disposed;

    // 委托必须用字段持有，否则会被 GC 回收，WndProc 回调直接崩。
    private WndProcDelegate? _wndProc;
    private static readonly HashSet<TrayIcon> Live = new();

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public bool IsAvailable { get; private set; }

    public TrayIcon(string iconPath, string tooltip, string openText, string exitText)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        _openText = openText;
        _exitText = exitText;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "RmlClient.Tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (_hwnd != IntPtr.Zero)
        {
            AddOrUpdate();
        }
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _wndProc = WindowProc;

        var className = "RmlClientTrayWnd";

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            return;
        }

        _hwnd = CreateWindowEx(0, className, "RmlClientTray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            UnregisterClass(className, wc.hInstance);
            return;
        }

        lock (Live)
        {
            Live.Add(this);
        }

        _icon = LoadAppIcon();
        IsAvailable = AddOrUpdate();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RemoveIcon();

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClass(className, wc.hInstance);

        lock (Live)
        {
            Live.Remove(this);
        }
    }

    private IntPtr LoadAppIcon()
    {
        // 优先从 exe 里取第一枚图标，取不到就退回系统默认图标。
        try
        {
            if (!string.IsNullOrEmpty(_iconPath) && File.Exists(_iconPath))
            {
                var count = ExtractIconEx(_iconPath, -1, null, null, 0);
                if (count > 0 && ExtractIconEx(_iconPath, 0, out var large, out _, 1) > 0 &&
                    large != IntPtr.Zero)
                {
                    return large;
                }
            }
        }
        catch (Exception)
        {
            // 忽略，走下面的兜底。
        }

        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private bool AddOrUpdate()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = Truncate(_tooltip, 127),
        };

        return Shell_NotifyIcon(NIM_ADD, ref data) || Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };

        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max];

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                var evt = (uint)(lParam.ToInt64() & 0xFFFF);
                if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
                {
                    Raise(OpenRequested);
                    return IntPtr.Zero;
                }

                if (evt == WM_RBUTTONUP)
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }

                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                // 资源管理器重启后任务栏会重建，必须重新注册图标。
                if (_taskbarCreated != 0 && msg == _taskbarCreated)
                {
                    AddOrUpdate();
                    return IntPtr.Zero;
                }

                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MF_STRING, IDM_OPEN, _openText);
            AppendMenu(menu, MF_STRING, IDM_EXIT, _exitText);

            GetCursorPos(out var pt);

            // 不先置前，菜单点外面不会消失（经典 Win32 托盘坑）。
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenu(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (cmd == IDM_OPEN)
            {
                Raise(OpenRequested);
            }
            else if (cmd == IDM_EXIT)
            {
                Raise(ExitRequested);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void Raise(Action? handler)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception)
        {
            // 托盘线程不能因为回调异常而死掉。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(2000);
        }
    }

    // ---------- Win32 ----------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private static readonly IntPtr IDI_APPLICATION = 32512;

    // 由系统广播，表示任务栏重建（资源管理器重启）。
    private static readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;

        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
