param(
    [string]$Text = '',
    [string]$Out = '',
    [int]$WaitMs = 1500
)
# Type text into the focused search box and screenshot.
# ASCII only: PS 5.1 -File reads scripts as ANSI.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not ('Typ3' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Typ3 {
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

[Typ3]::ShowWindow($hwnd, 9) | Out-Null
[Typ3]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
$edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
"edits=$($edits.Count)"

if ($edits.Count -lt 1) { 'no edit box'; exit 1 }
$edit = $edits[0]
$r = $edit.Current.BoundingRectangle
$x = [int]($r.Left + $r.Width / 2)
$y = [int]($r.Top + $r.Height / 2)

[Typ3]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 200
[Typ3]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[Typ3]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 400

$vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue($Text)
"typed: $Text"

Start-Sleep -Milliseconds $WaitMs

if ($Out) {
    $wr = New-Object Typ3+RECT
    [Typ3]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left
    $h = $wr.Bottom - $wr.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Typ3]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "saved $Out  ${w}x${h} printWindow=$ok"
}
