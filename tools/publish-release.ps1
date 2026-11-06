# Publishes a GitHub release with the installer, which is also what SensorPanel's About tab installs updates from.
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Notes notes.md [-SkipBuild]
#
# - The version comes from SensorPanel\SensorPanel.csproj (bump <Version> first); the release is tagged v<version> on
#   the current commit, which must be committed and pushed.
# - Builds Installer\out\SensorPanel-<version>-x64.msi (unless -SkipBuild) and uploads it. The About tab looks for the
#   latest release's .msi and checks it against the SHA-256 digest GitHub lists for it.
# - Publishes with the GitHub CLI (gh) if it's installed and signed in. Without it, signs in to GitHub with the
#   credentials git already uses for this repository (Git Credential Manager).
param(
    [Parameter(Mandatory = $true)][string]$Notes,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$csproj = Get-Content (Join-Path $root 'SensorPanel\SensorPanel.csproj')
    $version = @($csproj.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    $tag = "v$version"
    $body = Get-Content (Resolve-Path $Notes) -Raw -Encoding UTF8

    # owner/name from the remote, e.g. https://github.com/Owner/SensorPanel.git
    $remote = git remote get-url origin
    if ($remote -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') { throw "origin isn't a GitHub repository: $remote" }
    $repo = "$($Matches[1])/$($Matches[2])"

    # Release only what's on GitHub.
    if (git status --porcelain --untracked-files=no) { throw 'Commit your changes first.' }
    git fetch -q origin
    $head = git rev-parse HEAD
    if (-not (git branch -r --contains $head)) { throw 'Push this commit first.' }

    if (-not $SkipBuild) {
        & (Join-Path $root 'Installer\build-installer.ps1')
        if ($LASTEXITCODE) { throw 'The installer build failed.' }
    }
    $msi = Join-Path $root "Installer\out\SensorPanel-$version-x64.msi"
    if (-not (Test-Path $msi)) { throw "$msi is missing (build it, or leave out -SkipBuild)." }
    $built = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $root 'Installer\payload\SensorPanel.dll')).Version
    if ("$($built.Major).$($built.Minor).$($built.Build)" -ne $version) { throw "The installer contains SensorPanel $built, not $version." }
    $local = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    $gh = if ($gh) { $gh.Source } else { Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
    if (Test-Path $gh) {
        & $gh release view $tag --repo $repo --json url *> $null
        if ($LASTEXITCODE -eq 0) { throw "$tag is already released." }
        Write-Host "Creating release $tag on $repo with the GitHub CLI..."
        & $gh release create $tag $msi --repo $repo --target $head --title "SensorPanel $version" --notes-file (Resolve-Path $Notes) --latest
        if ($LASTEXITCODE) { throw 'The GitHub CLI could not create the release.' }
        $digest = & $gh api "repos/$repo/releases/tags/$tag" --jq '.assets[0].digest'
        if ($digest -and $digest -ne "sha256:$local") { throw "The uploaded file's checksum ($digest) doesn't match $local." }
        Write-Host "Installer checksum matches: $local"
        return
    }

    # The token git uses for github.com. Git only reads the request correctly from a file here (piped from Windows
    # PowerShell or .NET, it reports the protocol line missing).
    $request = Join-Path $env:TEMP "SensorPanel-credential-$PID.txt"
    [IO.File]::WriteAllBytes($request, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    try { $credential = cmd /c "git credential fill < `"$request`"" }
    finally { Remove-Item $request -ErrorAction SilentlyContinue }
    $token = ($credential | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
    if (-not $token) { throw 'No GitHub credentials stored for git; push once to sign in.' }
    $headers = @{
        Authorization          = "Bearer $token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }

    $existing = $null
    try { $existing = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers } catch { }
    if ($existing) { throw "$tag is already released: $($existing.html_url)" }

    Write-Host "Creating release $tag on $repo..."
    $json = @{ tag_name = $tag; target_commitish = $head; name = "SensorPanel $version"; body = $body; make_latest = 'true' } | ConvertTo-Json
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases" -Method Post -Headers $headers `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))

    Write-Host "Uploading $(Split-Path $msi -Leaf)..."
    $name = [Uri]::EscapeDataString((Split-Path $msi -Leaf))
    $asset = Invoke-RestMethod "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=$name" -Method Post `
        -Headers $headers -ContentType 'application/x-msi' -InFile $msi

    if ($asset.digest -and $asset.digest -ne "sha256:$local") { throw "The uploaded file's checksum ($($asset.digest)) doesn't match $local." }
    Write-Host "Released: $($release.html_url)"
    Write-Host "Installer: $($asset.browser_download_url)"
}
finally {
    Pop-Location
}
