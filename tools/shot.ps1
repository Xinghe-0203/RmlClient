# Screenshot helper: activate window -> optional click -> capture.
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads -File as ANSI (GB2312 here)
# and non-ASCII text breaks parsing unless the file has a BOM.
#
# Why PrintWindow and not CopyFromScreen: the app window is not topmost, so a screen
# copy picks up whatever overlaps it. PW_RENDERFULLCONTENT (2) captures the window's
# own composition surface regardless of z-order.
# Usage:
#   .\shot.ps1 -Out docs\screenshots\mods.png
#   .\shot.ps1 -Out docs\screenshots\mods.png -ClickX 110 -ClickY 202 -WaitAfterClickMs 2000
param(
    [string]$Out = 'docs\screenshots\shot.png',
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [int]$WaitMs = 700,
    [int]$WaitAfterClickMs = 2000
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Shot {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
'@

$proc = Get-Process RmlClient -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'RmlClient is not running' }
$h = $proc.MainWindowHandle
[Shot]::ShowWindow($h, 9) | Out-Null
[Shot]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds $WaitMs

$r = New-Object Shot+RECT
[Shot]::GetWindowRect($h, [ref]$r) | Out-Null

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [Shot]::SetCursorPos($r.Left + $ClickX, $r.Top + $ClickY) | Out-Null
    Start-Sleep -Milliseconds 200
    [Shot]::mouse_event([Shot]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Shot]::mouse_event([Shot]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds $WaitAfterClickMs
}

$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $hh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Shot]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out  ${w}x${hh}  at $($r.Left),$($r.Top)  printWindow=$ok"
