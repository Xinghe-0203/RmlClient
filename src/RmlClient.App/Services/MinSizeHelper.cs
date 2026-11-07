using System.Runtime.InteropServices;

namespace RmlClient.App.Services;

/// <summary>
/// 给 WinUI 3 窗口设置最小尺寸。
///
/// WinUI 3 没有暴露 AppWindow 的最小尺寸 API，只能拦 WM_GETMINMAXINFO。
/// 用 SetWindowSubclass 而不是 SetWindowLongPtr(GWLP_WNDPROC)，
/// 因为后者会与 WinUI 自己的消息处理冲突，且无法安全链式调用。
/// </summary>
internal static class MinSizeHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint SubclassId = 0x524D4C43; // "RMLC"

    private static readonly Dictionary<IntPtr, (int Width, int Height)> Constraints = new();
    private static readonly SubclassProc Proc = SubclassCallback;

    public static void Apply(IntPtr hwnd, int minWidth, int minHeight)
    {
        Constraints[hwnd] = (minWidth, minHeight);
        SetWindowSubclass(hwnd, Proc, SubclassId, IntPtr.Zero);
    }

    private static IntPtr SubclassCallback(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        nuint uIdSubclass,
        IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO && Constraints.TryGetValue(hWnd, out var limit))
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = limit.Width;
            info.ptMinTrackSize.Y = limit.Height;
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        uint uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        nuint uIdSubclass,
        IntPtr dwRefData);
}
