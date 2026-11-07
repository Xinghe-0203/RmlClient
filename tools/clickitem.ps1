param(
    [string]$TypeLike = 'ListItem',
    [int]$Index = 0,
    [string]$Out = '',
    [int]$WaitMs = 3000
)
# Click a list/grid item by real mouse (ItemClick does not respond to InvokePattern).
# ASCII only: PS 5.1 -File reads scripts as ANSI.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Click2' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Click2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

$proc = Get-Process -Name RmlClient -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { 'no window'; exit 1 }

$hwnd = $proc.MainWindowHandle
[Click2]::ShowWindow($hwnd, 9) | Out-Null
[Click2]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 600

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)

"items=$($items.Count)"
if ($items.Count -le $Index) { 'not enough items'; exit 1 }

$target = $items[$Index]
$r = $target.Current.BoundingRectangle
$x = [int]($r.Left + $r.Width / 2)
$y = [int]($r.Top + $r.Height / 2)
"target='$($target.Current.Name)' rect=$([int]$r.Left),$([int]$r.Top) $([int]$r.Width)x$([int]$r.Height) -> click $x,$y"

[Click2]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 200
[Click2]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[Click2]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)

Start-Sleep -Milliseconds $WaitMs

if ($Out) {
    $wr = New-Object Click2+RECT
    [Click2]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left
    $h = $wr.Bottom - $wr.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Click2]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "saved $Out  ${w}x${h} printWindow=$ok"
}
