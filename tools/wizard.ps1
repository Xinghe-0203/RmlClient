# Inno Setup wizard: its controls are plain Win32 child windows, and UI Automation
# reports the buttons as anonymous Panes with no Invoke pattern. Drive them directly
# with EnumChildWindows + BM_CLICK / WM_SETTEXT instead.
# NOTE: keep this file ASCII-only (PowerShell 5.1 -File parses as ANSI here).
param(
    [int]$ProcessId = 0,
    [string]$TitleLike = '*Raft Mod Loader*',
    [string]$ClickText = '',
    [string]$ClickIndex = 0,
    [string]$ButtonClass = 'TNewButton|Button',
    [string]$SetEditText = '',
    [string]$Out = '',
    [int]$WaitMs = 1500
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Win32Ctl {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageStr(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<string> Dump(IntPtr parent) {
        var list = new List<string>();
        EnumChildWindows(parent, (h, p) => {
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            list.Add(h.ToInt64() + "|" + c.ToString() + "|" + t.ToString());
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr FindByText(IntPtr parent, string text, string cls) {
        // cls may list several classes separated by '|' (the wizard uses TNewButton,
        // the uninstaller confirmation dialog uses plain Button).
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, p) => {
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            bool clsOk = cls == "" || ("|" + cls + "|").Contains("|" + c.ToString() + "|");
            if (t.ToString().Contains(text) && clsOk) {
                found = h; return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindByClass(IntPtr parent, string cls) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, p) => {
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            if (c.ToString() == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

if ($ProcessId -gt 0) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
} else {
    $proc = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like $TitleLike } |
        Select-Object -First 1
}
if (-not $proc) { throw 'target window not found' }

$h = $proc.MainWindowHandle
if ($h -eq 0) { throw "process $($proc.Id) has no main window" }
Write-Output "window: pid=$($proc.Id) hwnd=$h title='$($proc.MainWindowTitle)'"

if ($SetEditText) {
    # the folder edit box in the "select destination" page
    $edit = [Win32Ctl]::FindByClass($h, 'Edit')
    if ($edit -eq [IntPtr]::Zero) { throw 'no Edit control' }
    [Win32Ctl]::SendMessageStr($edit, 0x000C, [IntPtr]::Zero, $SetEditText) | Out-Null
    Write-Output "set edit text: $SetEditText"
    Start-Sleep -Milliseconds 300
}

if ($ClickText) {
    $btn = [Win32Ctl]::FindByText($h, $ClickText, $ButtonClass)
    if ($btn -eq [IntPtr]::Zero) {
        Write-Output '--- child windows ---'
        foreach ($line in [Win32Ctl]::Dump($h)) { Write-Output "  $line" }
        throw "no TNewButton containing '$ClickText'"
    }
    $enabled = [Win32Ctl]::IsWindowEnabled($btn)
    Write-Output "button hwnd=$($btn.ToInt64()) enabled=$enabled"
    [Win32Ctl]::SendMessageW($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Write-Output 'action: BM_CLICK'
}

Start-Sleep -Milliseconds $WaitMs

if ($Out) {
    $r = New-Object Win32Ctl+RECT
    [Win32Ctl]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Win32Ctl]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved $Out printWindow=$ok"
}
