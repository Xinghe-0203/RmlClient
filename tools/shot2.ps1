# Capture the RmlClient main window via PrintWindow (z-order independent).
# Pure ASCII only: PowerShell 5.1 parses -File scripts as ANSI.
param(
    [string]$Out = 'shot.png',
    [string]$TitleLike = 'Raft Mod Loader',
    [int]$WaitMs = 300
)

Add-Type -AssemblyName System.Drawing

if (-not ('ShotLib' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class ShotLib {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Title(IntPtr h) {
        int len = GetWindowTextLength(h);
        var sb = new StringBuilder(len + 2);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<IntPtr> Find(string needle) {
        var found = new List<IntPtr>();
        EnumWindows((h, p) => {
            if (!IsWindowVisible(h)) return true;
            var t = Title(h);
            if (t.Length > 0 && t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Capture(IntPtr h, string path) {
        // A minimized window reports rect (-32000,-32000,160,28); restore first.
        if (IsIconic(h)) {
            ShowWindow(h, 9);
            SetForegroundWindow(h);
            System.Threading.Thread.Sleep(500);
        }

        RECT r;
        if (!GetWindowRect(h, out r)) return "GetWindowRect failed";
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return "bad rect " + w + "x" + ht;
        using (var bmp = new System.Drawing.Bitmap(w, ht)) {
            using (var g = System.Drawing.Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(h, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!ok) return "PrintWindow failed";
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        return "saved " + w + "x" + ht;
    }
}
"@
}

$handles = [ShotLib]::Find($TitleLike)
if ($handles.Count -eq 0) { throw "no window matching '$TitleLike'" }

$h = $handles[0]
"window: hwnd=$h title='$([ShotLib]::Title($h))'"
Start-Sleep -Milliseconds $WaitMs

$full = [System.IO.Path]::GetFullPath($Out)
[ShotLib]::Capture($h, $full)
