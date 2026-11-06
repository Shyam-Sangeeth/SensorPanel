# Builds SensorPanel into ..\App (a copy built from source; the installed copy is the one to run day to day).
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

# No Start menu shortcut: the installed copy has one. Remove the ones older versions of this script made.
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
foreach ($name in 'SensorPanel.lnk', 'SensorPanel (App build).lnk') {
    $old = Join-Path $programs $name
    if ((Test-Path $old) -and $shell.CreateShortcut($old).TargetPath -eq (Join-Path $Output 'SensorPanel.exe')) { Remove-Item $old }
}

"SensorPanel is in $Output"
