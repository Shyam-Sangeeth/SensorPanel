# Builds SensorPanel into ..\App (the copy to run day to day) and adds a Start menu shortcut.
# Close SensorPanel first if it's running from ..\App (tray icon > Exit).
param([string]$Output = (Join-Path $PSScriptRoot '..\App'))

$ErrorActionPreference = 'Stop'
$Output = [System.IO.Path]::GetFullPath($Output)

# A separate build folder, so a copy running from bin\ doesn't block the build.
$bin = Join-Path $PSScriptRoot 'obj\publish-bin\'
dotnet publish (Join-Path $PSScriptRoot 'SensorPanel.csproj') -c Release -o $Output "-p:BaseOutputPath=$bin" --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# Display plugins, each in its own folder under plugins\.
foreach ($name in 'Ax206', 'ImageFile') {
    $plugin = Join-Path $PSScriptRoot "..\Plugins\$name\$name.csproj"
    dotnet publish $plugin -c Release -o (Join-Path $Output "plugins\$name") --nologo
    if ($LASTEXITCODE -ne 0) { throw "$name plugin build failed." }
}

# Named apart from the installer's "SensorPanel" shortcut, so Start search doesn't open this copy by mistake (and
# "Start with Windows" ticked here moves the startup task to this copy).
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$old = Join-Path $programs 'SensorPanel.lnk'
if ((Test-Path $old) -and $shell.CreateShortcut($old).TargetPath -eq (Join-Path $Output 'SensorPanel.exe')) { Remove-Item $old }
$shortcut = Join-Path $programs 'SensorPanel (App build).lnk'
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $Output 'SensorPanel.exe'
$link.WorkingDirectory = $Output
$link.Description = 'SensorPanel built from source (the App folder)'
$link.Save()

"SensorPanel is in $Output"
"Start menu shortcut: $shortcut"
