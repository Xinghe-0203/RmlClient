# Click a UI element by its automation name (substring match).
# NOTE: keep this file ASCII-only (PowerShell 5.1 -File parses as ANSI here).
param(
    [string]$NameLike,
    [string]$AutomationId = '',
    [int]$Index = 0,
    [string]$Out = '',
    [int]$WaitMs = 3000,
    [string]$TypeLike = 'Button|CheckBox|RadioButton',
    [switch]$Exact
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class ClickShot {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$proc = Get-Process RmlClient -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'RmlClient is not running' }
$h = $proc.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)

$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$hits = @()
foreach ($el in $all) {
    try {
        $n = $el.Current.Name
        $id = $el.Current.AutomationId
        $t = $el.Current.ControlType.ProgrammaticName
        if ($AutomationId) {
            if ($id -ne $AutomationId) { continue }
        } else {
            if ($t -notmatch $TypeLike) { continue }
            if ($NameLike) {
                if ($Exact) {
                    if ($n -ne $NameLike) { continue }
                } else {
                    if (-not ($n -like "*$NameLike*")) { continue }
                }
            }
        }
        $hits += $el
    } catch { }
}

Write-Output "matches for '$NameLike': $($hits.Count)"
if ($hits.Count -eq 0) { throw "no element matching '$NameLike'" }

$target = $hits[$Index]
Write-Output "target: '$($target.Current.Name)' ($($target.Current.ControlType.ProgrammaticName))"

# CheckBox / RadioButton 走 TogglePattern / SelectionItemPattern，
# Button 才支持 InvokePattern，用错会抛 InvalidOperationException。
# 这里不用 TryGetCurrentPattern：PowerShell 5.1 对它的 out 参数支持有坑
# （会报 NonExistingVariableReference），改成逐个 GetCurrentPattern + try/catch。
$acted = $false

try {
    $target.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Write-Output 'action: toggle'
    $acted = $true
} catch { }

if (-not $acted) {
    try {
        $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Write-Output 'action: select'
        $acted = $true
    } catch { }
}

if (-not $acted) {
    try {
        $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output 'action: invoke'
        $acted = $true
    } catch { }
}

if (-not $acted) { throw 'element supports none of Toggle/SelectionItem/Invoke' }

Start-Sleep -Milliseconds $WaitMs

if ($Out) {
    $r = New-Object ClickShot+RECT
    [ClickShot]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [ClickShot]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved $Out printWindow=$ok"
}
