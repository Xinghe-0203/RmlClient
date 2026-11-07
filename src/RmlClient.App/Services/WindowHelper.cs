using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace RmlClient.App.Services;

/// <summary>窗口外观相关的辅助：Mica 背景、自定义标题栏、居中、暗色标题栏。</summary>
public static class WindowHelper
{
    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    public static IntPtr GetHwnd(Window window) => WindowNative.GetWindowHandle(window);

    /// <summary>尝试启用 Mica（Win11 22H2+）；失败时静默回退到纯色背景。</summary>
    public static bool TryEnableMica(Window window, bool dark)
    {
        try
        {
            var rootElement = window.Content as FrameworkElement;
            if (rootElement is null)
            {
                return false;
            }

            window.SystemBackdrop = new MicaBackdrop
            {
                Kind = dark ? MicaKind.BaseAlt : MicaKind.Base,
            };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把窗口设置为「无系统标题栏、自绘标题栏」。</summary>
    public static void UseCustomTitleBar(Window window, UIElement titleBarElement)
    {
        try
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBarElement);
        }
        catch (Exception)
        {
            // 某些系统版本不支持自绘标题栏，忽略即可。
        }
    }

    public static void Resize(Window window, int width, int height)
    {
        var appWindow = GetAppWindow(window);
        var scale = GetScale(window);
        appWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    public static void SetMinSize(Window window, int width, int height)
    {
        var hwnd = GetHwnd(window);
        var scale = GetScale(window);
        MinSizeHelper.Apply(hwnd, (int)(width * scale), (int)(height * scale));
    }

    public static void CenterOnScreen(Window window)
    {
        var appWindow = GetAppWindow(window);
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;

        appWindow.Move(new PointInt32(
            work.X + (work.Width - appWindow.Size.Width) / 2,
            work.Y + (work.Height - appWindow.Size.Height) / 2));
    }

    public static void SetTitleBarColors(Window window, bool dark)
    {
        var appWindow = GetAppWindow(window);
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 230, 237, 247)
            : Windows.UI.Color.FromArgb(255, 15, 23, 42);
        titleBar.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
            : Windows.UI.Color.FromArgb(20, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(60, 255, 255, 255)
            : Windows.UI.Color.FromArgb(30, 0, 0, 0);
    }

    /// <summary>把窗口提到最前（从托盘恢复时需要，否则可能被其它窗口压住）。</summary>
    public static void BringToFront(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            NativeMethods.ShowWindow(hwnd, 9 /* SW_RESTORE */);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception)
        {
            // 前台权限被系统拒绝时忽略即可，窗口本身已经 Show 出来。
        }
    }

    private static double GetScale(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            return dpi <= 0 ? 1.0 : dpi / 96.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
