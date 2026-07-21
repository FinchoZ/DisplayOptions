<#
Builds the mod and zips it into a release-ready archive at
dist/DisplayOptions-v<version>.zip (containing About/ and Assemblies/).

-SkipBuild packages the Assemblies/ already committed to the repo instead of
recompiling. RimWorld's own DLLs can't be built against on a CI runner (they're
Ludeon's copyrighted files, not something we can ship or fetch there), so this
is what .github/workflows/release.yml uses - the actual mod DLL is built
locally and committed, and CI just packages + publishes it.

-Publish tags and pushes vX.Y.Z, which triggers that workflow to build the zip
and create the GitHub Release - it does not create the release itself, so it
doesn't need `gh` auth locally.
#>

param(
    [string]$RimWorldDir = "",
    [string]$Version,
    [switch]$SkipBuild,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $Version) {
    $Version = (Get-Content (Join-Path $root "VERSION") -Raw).Trim()
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build.ps1") -RimWorldDir $RimWorldDir
}

$distDir = Join-Path $root "dist"
$stageDir = Join-Path $distDir "DisplayOptions"
$zipPath = Join-Path $distDir "DisplayOptions-v$Version.zip"

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $root "About") (Join-Path $stageDir "About") -Recurse
Copy-Item (Join-Path $root "Assemblies") (Join-Path $stageDir "Assemblies") -Recurse

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath
Remove-Item $stageDir -Recurse -Force

Write-Host "Packaged $zipPath"

if ($Publish) {
    git tag "v$Version"
    git push origin "v$Version"
    Write-Host "Pushed tag v$Version - GitHub Actions will build and publish the release."
}
