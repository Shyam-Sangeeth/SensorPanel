# Tests the update check against the real GitHub releases, without installing anything: builds a copy of SensorPanel
# that calls itself an older version, then checks that it finds the latest release
#   1. when "Check for updates" is clicked on the About tab, and
#   2. automatically, about 20 seconds after starting with the window closed (a "SensorPanel X is available"
#      notification appears on screen during this one).
#   powershell -ExecutionPolicy Bypass -File tools\test-update-check.ps1 [-Version 0.9.0] [-SkipAutomatic]
#
# The test copy runs without administrator rights and uses the normal panel and settings, which are backed up and put
# back afterwards. A running SensorPanel is closed first and started again at the end (an administrator prompt).
# "Download and install" is never clicked: it would install SensorPanel into Program Files.
param([string]$Version = '0.9.0', [switch]$SkipAutomatic)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'SensorPanel\SensorPanel.csproj'
$bin = Join-Path $root 'SensorPanel\obj\update-test\'
$exe = Join-Path $bin 'Release\net10.0-windows\win-x64\SensorPanel.exe'
$data = Join-Path $env:APPDATA 'SensorPanel'
$backup = Join-Path $env:TEMP 'SensorPanel-update-test'

Write-Host "Building a test copy that calls itself $Version..."
dotnet build $project -c Release "-p:BaseOutputPath=$bin" "-p:Version=$Version" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet build-server shutdown | Out-Null
$plugins = Join-Path $root 'App\plugins'
if (Test-Path $plugins) {
    # The display plugins, so the test copy uses the display as usual.
    $target = New-Item -ItemType Directory -Force (Join-Path (Split-Path $exe) 'plugins')
    Copy-Item "$plugins\*" $target.FullName -Recurse -Force
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function Find-All($element, $type) {
    $element.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $type)))
}
function Wait-Window($process) {
    foreach ($i in 1..40) { Start-Sleep -Milliseconds 500; $process.Refresh(); if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break } }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw "The test copy's window didn't open." }
    Start-Sleep 2
    return $UIA::FromHandle($process.MainWindowHandle)
}
# The About tab's update status and buttons (the tab's contents only exist while it's selected).
function Read-About($window) {
    $tab = Find-All $window $CT::TabItem | Where-Object { $_.Current.Name -eq 'About' }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep 1
    $status = Find-All $window $CT::Text | ForEach-Object { $_.Current.Name } |
        Where-Object { $_ -match 'is available|up to date|Couldn''t check|You have|Checking' } | Select-Object -First 1
    $install = Find-All $window $CT::Button | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like 'Download and install*' }
    return @{ Status = $status; Install = $install }
}
function Stop-TestCopy($process) {
    try { [Threading.EventWaitHandle]::OpenExisting('SensorPanel.Exit').Set() | Out-Null } catch { }
    $process.WaitForExit(10000) | Out-Null
}
function Report($name, $about) {
    $ok = $about.Status -like '*is available*' -and $about.Install
    $result = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0}: {1}  [{2}]  [{3}]" -f $result, $name, $about.Status, ($about.Install -join ', '))
    return $ok
}

# Close the running copy (it would take the test copy's place) and keep the panel and settings safe.
$wasRunning = [bool](Get-Process SensorPanel -ErrorAction SilentlyContinue)
try { [Threading.EventWaitHandle]::OpenExisting('SensorPanel.Exit').Set() | Out-Null } catch { }
foreach ($i in 1..20) { if (-not (Get-Process SensorPanel -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }
New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($file in 'settings.json', 'panel.json') {
    if (Test-Path "$data\$file") { Copy-Item "$data\$file" "$backup\$file" -Force }
}

$passed = $true
try {
    Write-Host 'Test 1: Check for updates on the About tab...'
    $p = Start-Process $exe -ArgumentList '--no-elevate' -PassThru
    $window = Wait-Window $p
    Read-About $window | Out-Null
    $check = Find-All $window $CT::Button | Where-Object { $_.Current.Name -eq 'Check for updates' }
    $check.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 5
    $passed = (Report 'Check for updates' (Read-About $window)) -and $passed
    Stop-TestCopy $p

    if (-not $SkipAutomatic) {
        Write-Host 'Test 2: automatic check with the window closed (about 30 seconds; watch for the notification)...'
        $p = Start-Process $exe -ArgumentList '--no-elevate', '--minimized' -PassThru
        Start-Sleep 32
        [Threading.EventWaitHandle]::OpenExisting('SensorPanel.ShowWindow').Set() | Out-Null
        $window = Wait-Window $p
        $passed = (Report 'Automatic check' (Read-About $window)) -and $passed
        Stop-TestCopy $p
    }
}
finally {
    foreach ($file in 'settings.json', 'panel.json') {
        if (Test-Path "$backup\$file") { Copy-Item "$backup\$file" "$data\$file" -Force }
    }
    if ($wasRunning) {
        $app = Join-Path $root 'App\SensorPanel.exe'
        Write-Host 'Starting SensorPanel again...'
        if (Test-Path $app) { Start-Process $app }
    }
}
if (-not $passed) { exit 1 }
