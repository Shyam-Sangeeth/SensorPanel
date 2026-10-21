# Builds the SensorPanel installer: publishes a self-contained build and its plugins into Installer\payload, then
# packages it as Installer\out\SensorPanel-<version>-x64.msi.
#   powershell -ExecutionPolicy Bypass -File Installer\build-installer.ps1 [-Version 1.2.0]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'SensorPanel\SensorPanel.csproj'

if (-not $Version) {
    [xml]$xml = Get-Content $project
    $Version = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}

$payload = Join-Path $PSScriptRoot 'payload'
$out = Join-Path $PSScriptRoot 'out'
Remove-Item $payload, $out -Recurse -Force -ErrorAction SilentlyContinue

# A separate build folder, so a copy running from SensorPanel\bin doesn't block the build.
$bin = Join-Path $PSScriptRoot 'obj\publish-bin\'

Write-Host "Publishing SensorPanel $Version (self-contained, win-x64)..."
dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$Version "-p:BaseOutputPath=$bin" -o $payload --nologo
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

foreach ($name in 'Ax206', 'ImageFile') {
    Write-Host "Publishing the $name plugin..."
    dotnet publish (Join-Path $root "Plugins\$name\$name.csproj") -c Release -o (Join-Path $payload "plugins\$name") --nologo
    if ($LASTEXITCODE) { throw "$name plugin build failed" }
}

Write-Host 'Building the installer...'
dotnet build (Join-Path $PSScriptRoot 'SensorPanel.Installer.wixproj') -c Release -p:Version=$Version -p:PayloadDir=$payload -o $out --nologo
if ($LASTEXITCODE) { throw 'Installer build failed' }

Get-ChildItem $out -Filter *.msi | Select-Object Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } }, FullName
