# Retakes the README screenshots in docs\ from the App\ build: the Editor, Settings and About tabs, and the panel cropped
# from the preview.
#   powershell -ExecutionPolicy Bypass -File tools\capture-screenshots.ps1
#
# SensorPanel needs administrator rights for all its sensors, and an elevated window can only be captured from an
# elevated script, so this asks for administrator rights (one prompt). A running SensorPanel is closed first, then
# started again from App\ and left running on the Editor tab. Windows are captured with PrintWindow, so other windows on
# top don't matter.
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'App\SensorPanel.exe'
$docs = Join-Path $root 'docs'
if (-not (Test-Path $exe)) { throw "Build SensorPanel first (SensorPanel\build.ps1): $exe is missing." }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($Elevated) { throw 'Administrator rights were not granted.' }
    $log = Join-Path $env:TEMP 'SensorPanel-capture.log'
    $p = Start-Process powershell -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
        "& '$PSCommandPath' -Elevated *> '$log'")
    $p.WaitForExit() # not Start-Process -Wait: that also waits for SensorPanel, which keeps running
    Get-Content $log
    return
}

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CaptureWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
}
'@
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

# The window as it looks on screen (without the shadow), and where that is on screen.
function Capture-Window($hwnd) {
    $r = New-Object CaptureWin+RECT; [CaptureWin]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $f = New-Object CaptureWin+RECT; [CaptureWin]::DwmGetWindowAttribute($hwnd, 9, [ref]$f, 16) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [CaptureWin]::PrintWindow($hwnd, $hdc, 2) | Out-Null # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    $frame = New-Object System.Drawing.Rectangle ($f.L - $r.L), ($f.T - $r.T), ($f.R - $f.L), ($f.B - $f.T)
    $image = $bmp.Clone($frame, $bmp.PixelFormat); $bmp.Dispose()
    return @{ Image = $image; Left = $f.L; Top = $f.T }
}

function Select-Tab($window, $name) {
    $tab = $window.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::TabItem))) |
        Where-Object { $_.Current.Name -eq $name }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep 1
}

# Close a running copy, then start App\ (as administrator, like this script).
try { [Threading.EventWaitHandle]::OpenExisting('SensorPanel.Exit').Set() | Out-Null } catch { }
foreach ($i in 1..20) { if (-not (Get-Process SensorPanel -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }
$app = Start-Process $exe -PassThru
foreach ($i in 1..40) { Start-Sleep -Milliseconds 500; $app.Refresh(); if ($app.MainWindowHandle -ne [IntPtr]::Zero) { break } }
if ($app.MainWindowHandle -eq [IntPtr]::Zero) { throw "SensorPanel's window didn't open." }
Start-Sleep 8 # sensor values and graphs fill in
$window = $UIA::FromHandle($app.MainWindowHandle)
New-Item -ItemType Directory -Force $docs | Out-Null

Select-Tab $window 'Editor'
$shot = Capture-Window $app.MainWindowHandle
$shot.Image.Save((Join-Path $docs 'editor.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview = $window.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, 'PreviewImage')))
if ($preview) {
    $b = $preview.Current.BoundingRectangle
    $area = New-Object System.Drawing.Rectangle ([int]$b.X - $shot.Left), ([int]$b.Y - $shot.Top), ([int]$b.Width), ([int]$b.Height)
    $panel = $shot.Image.Clone($area, $shot.Image.PixelFormat)
    $panel.Save((Join-Path $docs 'panel.png'), [System.Drawing.Imaging.ImageFormat]::Png); $panel.Dispose()
}
else { Write-Warning "Couldn't find the preview; panel.png wasn't updated." }
$shot.Image.Dispose()

foreach ($tab in 'Settings', 'About') {
    Select-Tab $window $tab
    $shot = Capture-Window $app.MainWindowHandle
    $shot.Image.Save((Join-Path $docs "$($tab.ToLower()).png"), [System.Drawing.Imaging.ImageFormat]::Png); $shot.Image.Dispose()
}
Select-Tab $window 'Editor'
Get-ChildItem $docs -Filter *.png | ForEach-Object { "Saved $($_.FullName)" }
