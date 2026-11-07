# UI Automation driver for the RML client.
# NOTE: keep this file ASCII-only (PowerShell 5.1 -File parses as ANSI here).
# Screenshots use PrintWindow so the result does not depend on z-order.
param(
    [string]$Nav,                      # automation id of nav button, e.g. nav.market
    [string]$Out = '',
    [int]$WaitMs = 1200
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class UiaShot {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$proc = Get-Process RmlClient -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'RmlClient is not running' }
$h = $proc.MainWindowHandle
[UiaShot]::ShowWindow($h, 9) | Out-Null

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)

if ($Nav) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Nav)
    $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $btn) { throw "nav button '$Nav' not found" }
    $pattern = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Output "invoked $Nav"
    Start-Sleep -Milliseconds $WaitMs
}

if ($Out) {
    $r = New-Object UiaShot+RECT
    [UiaShot]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [UiaShot]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved $Out  ${w}x${hh}  printWindow=$ok"
}
