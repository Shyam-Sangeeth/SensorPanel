# Builds the Picture file plugin into SensorPanel's plugins folder (App\plugins\ImageFile). Restart SensorPanel afterwards.
# SensorPanel\build.ps1 also builds it, along with SensorPanel and the other plugins.
$ErrorActionPreference = 'Stop'
$target = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\App\plugins\ImageFile'))
dotnet publish (Join-Path $PSScriptRoot 'ImageFile.csproj') -c Release -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
"Installed in $target. Restart SensorPanel to use it."
