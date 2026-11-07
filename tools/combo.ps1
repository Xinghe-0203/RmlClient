param(
    [string]$ComboName = '',
    [int]$ComboIndex = 0,
    [string]$ItemName = '',
    [string]$Out = '',
    [int]$WaitMs = 3000
)
# Expand a ComboBox in the dialog and select an item by name.
# ASCII only: PS 5.1 -File reads scripts as ANSI.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('Combo2' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Combo2 {
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

function Click-Rect($r) {
    $x = [int]($r.Left + $r.Width / 2)
    $y = [int]($r.Top + $r.Height / 2)
    [Combo2]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 200
    [Combo2]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Combo2]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
}

$proc = Get-Process -Name RmlClient -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { 'no window'; exit 1 }
$hwnd = $proc.MainWindowHandle

[Combo2]::ShowWindow($hwnd, 9) | Out-Null
[Combo2]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ComboBox)
$combos = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
"combos=$($combos.Count)"
foreach ($c in $combos) { "  [$($c.Current.Name)] $([int]$c.Current.BoundingRectangle.Left),$([int]$c.Current.BoundingRectangle.Top)" }

if ($combos.Count -le $ComboIndex) { 'no combo'; exit 1 }
$combo = $combos[$ComboIndex]

$ep = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
$ep.Expand()
Start-Sleep -Milliseconds 800

# Items live in a popup - search from the desktop root, not the window.
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$all = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
"popup items=$($all.Count)"

$picked = $false
foreach ($it in $all) {
    if ($it.Current.Name -eq $ItemName) {
        $sel = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        $picked = $true
        "selected '$ItemName'"
        break
    }
}

if (-not $picked) {
    "item '$ItemName' not found; available:"
    foreach ($it in $all) { "  '$($it.Current.Name)'" }
    $ep.Collapse()
    exit 1
}

Start-Sleep -Milliseconds $WaitMs

if ($Out) {
    $wr = New-Object Combo2+RECT
    [Combo2]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left
    $h = $wr.Bottom - $wr.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Combo2]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "saved $Out  ${w}x${h} printWindow=$ok"
}
