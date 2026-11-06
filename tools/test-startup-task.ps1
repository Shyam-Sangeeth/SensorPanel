# Tests the Start with Windows task with a test copy of SensorPanel:
#   1. a copy the task doesn't start leaves the task alone;
#   2. a task made before 1.1.1 (no restart after a crash) is updated when the copy it starts runs;
#   3. after a crash the task starts SensorPanel again, and after 3 crashes in 5 minutes it shows the "stopped" notice
#      instead (a notification appears on screen briefly).
#   powershell -ExecutionPolicy Bypass -File tools\test-startup-task.ps1
#
# Crashes are made by starting a thread at address 0 in the test copy. The task, panel, settings and errors.log are
# backed up and put back afterwards, and a running SensorPanel is closed first and started again at the end. Asks for
# administrator rights (one prompt): the task runs SensorPanel as administrator.
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'SensorPanel\SensorPanel.csproj'
$test = Join-Path $root 'SensorPanel\obj\startup-test\Release\net10.0-windows\win-x64\SensorPanel.exe'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($Elevated) { throw 'Administrator rights were not granted.' }
    Write-Host 'Building the test copy...'
    dotnet build $project -c Release "-p:BaseOutputPath=$(Join-Path $root 'SensorPanel\obj\startup-test\')" -v q -nologo | Out-Null
    if ($LASTEXITCODE) { throw 'The build failed.' }
    $log = Join-Path $env:TEMP 'SensorPanel-startup-test.log'
    $p = Start-Process powershell -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
        "& '$PSCommandPath' -Elevated *> '$log'")
    $p.WaitForExit() # not Start-Process -Wait: that also waits for SensorPanel, which keeps running
    Get-Content $log
    if (Select-String -Path $log -Pattern '^FAIL' -Quiet) { exit 1 }
    return
}

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Crash {
  [DllImport("kernel32")] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
  [DllImport("kernel32")] public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, int flags, IntPtr tid);
  [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);
}
'@

$data = Join-Path $env:APPDATA 'SensorPanel'
$backup = Join-Path $env:TEMP "SensorPanel-startup-test-$PID"
New-Item -ItemType Directory -Force $backup | Out-Null
$failed = $false

function Check($ok, $what) {
    if ($ok) { "PASS $what" } else { "FAIL $what"; $script:failed = $true }
}
function TaskXml { try { [xml](Export-ScheduledTask -TaskName SensorPanel -ErrorAction Stop) } catch { $null } }
function TaskCommand { $x = TaskXml; if ($x) { $x.Task.Actions.Exec.Command } }
function HasCrashTrigger { $x = TaskXml; [bool]($x -and $x.Task.Triggers.EventTrigger) }
# The full app has this event; the "stopped" notice doesn't.
function FullAppRunning {
    $h = $null
    $ok = [Threading.EventWaitHandle]::TryOpenExisting('SensorPanel.ShowWindow', [ref]$h)
    if ($h) { $h.Dispose() }
    $ok
}
function StopAll {
    try { [Threading.EventWaitHandle]::OpenExisting('SensorPanel.Exit').Set() | Out-Null } catch { }
    foreach ($i in 1..40) { if (-not (Get-Process SensorPanel -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 250 }
    Get-Process SensorPanel -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
}
function WaitForStart($seconds) {
    foreach ($i in 1..$seconds) {
        Start-Sleep 1
        $p = Get-Process SensorPanel -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) { Start-Sleep 4; return $p } # let it get past start-up
    }
}
function CrashProcess($p) {
    $h = [Crash]::OpenProcess(0x1F0FFF, $false, $p.Id)
    [Crash]::CreateRemoteThread($h, [IntPtr]::Zero, [UIntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, 0, [IntPtr]::Zero) | Out-Null
    [Crash]::CloseHandle($h) | Out-Null
    $p.WaitForExit(15000) | Out-Null
}

# Crashes from an earlier run still count for 5 minutes and would stop the test copy from starting.
$recent = Get-WinEvent -LogName Application -ErrorAction SilentlyContinue -FilterXPath ("*[System[Provider[@Name='Application Error'] and " +
    "EventID=1000 and TimeCreated[timediff(@SystemTime) <= 300000]]] and *[EventData[Data[@Name='AppPath']='$test']]")
if ($recent) {
    "FAIL the test copy crashed in the last 5 minutes (an earlier run); run again after $(($recent[0].TimeCreated).AddMinutes(5).ToString('HH:mm:ss'))"
    return
}

# Back up, and remember what was running.
$running = Get-CimInstance Win32_Process -Filter "Name='SensorPanel.exe'" | Select-Object -First 1 -ExpandProperty ExecutablePath
$hadTask = [bool](TaskXml)
if ($hadTask) { (TaskXml).OuterXml | Out-File -Encoding Unicode (Join-Path $backup 'task.xml') }
foreach ($f in 'panel.json', 'settings.json', 'errors.log') {
    if (Test-Path (Join-Path $data $f)) { Copy-Item (Join-Path $data $f) $backup }
}

try {
    StopAll

    # 1. A copy the task doesn't start leaves it alone.
    if ($hadTask) {
        $before = TaskCommand
        Start-Process $test '--minimized'
        WaitForStart 20 | Out-Null
        Start-Sleep 5
        Check ((TaskCommand) -eq $before) "a copy the task doesn't start leaves it alone ($before)"
        StopAll
    }
    else { 'SKIP 1: there was no startup task to leave alone' }

    # 2. A task made before 1.1.1 is updated by the copy it starts.
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $old = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts SensorPanel for the USB sensor display.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$user</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>
  <Actions Context="Author"><Exec><Command>$test</Command><Arguments>--minimized</Arguments></Exec></Actions>
</Task>
"@
    $old | Out-File -Encoding Unicode (Join-Path $backup 'old-task.xml')
    schtasks /Create /TN SensorPanel /XML (Join-Path $backup 'old-task.xml') /F | Out-Null
    Check (-not (HasCrashTrigger)) 'the old-style task has no crash trigger'
    Start-Process $test '--minimized'
    $p = WaitForStart 20
    Start-Sleep 5
    Check ((HasCrashTrigger) -and (TaskCommand) -eq $test) 'the old-style task is updated by the copy it starts'

    # 3. Crashes: started again twice, then the "stopped" notice.
    foreach ($n in 1..3) {
        CrashProcess $p
        $p = WaitForStart 60
        if (-not $p) { Check $false "started again after crash $n"; break }
        if ($n -lt 3) { Check (FullAppRunning) "started again after crash $n" }
        else { Check (-not (FullAppRunning)) 'after crash 3 in 5 minutes, only the "stopped" notice starts' }
    }
    $logged = (Test-Path (Join-Path $data 'errors.log')) -and
        (Select-String -Path (Join-Path $data 'errors.log') -Pattern 'Not started again' -Quiet)
    Check $logged 'errors.log says why it was not started again'
}
catch { "FAIL $_"; $failed = $true }
finally {
    StopAll
    if ($hadTask) { schtasks /Create /TN SensorPanel /XML (Join-Path $backup 'task.xml') /F | Out-Null }
    else { schtasks /Delete /TN SensorPanel /F 2>$null | Out-Null }
    foreach ($f in 'panel.json', 'settings.json', 'errors.log') {
        if (Test-Path (Join-Path $backup $f)) { Copy-Item (Join-Path $backup $f) $data -Force }
        elseif (Test-Path (Join-Path $data $f)) { Remove-Item (Join-Path $data $f) }
    }
    "Put back: startup task ($(if ($hadTask) { TaskCommand } else { 'none' })), panel, settings, errors.log"
    if ($running) {
        if ($hadTask -and (TaskCommand) -eq $running) { schtasks /Run /TN SensorPanel | Out-Null }
        else { Start-Process $running '--minimized' }
        "Started SensorPanel again: $running"
    }
    Remove-Item $backup -Recurse -Force
}
