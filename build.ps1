<#
Builds the Display Options mod without needing the .NET SDK / MSBuild.
Compiles directly with the legacy csc.exe against RimWorld's own Managed DLLs.
#>

param(
    [string]$RimWorldDir = "",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

if (-not $RimWorldDir) {
    throw "Pass -RimWorldDir 'X:\path\to\RimWorld'."
}

$managed = Join-Path $RimWorldDir "RimWorldWin64_Data\Managed"
if (-not (Test-Path $managed)) {
    throw "Could not find RimWorld's Managed folder at '$managed'. Pass -RimWorldDir 'X:\path\to\RimWorld'."
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at '$csc'."
}

$root = $PSScriptRoot
$src = Join-Path $root "src\DisplayOptions\DisplayOptionsMod.cs"
$harmony = Join-Path $root "src\Lib\0Harmony.dll"
$outDir = Join-Path $root "Assemblies"
$out = Join-Path $outDir "DisplayOptions.dll"

if (-not (Test-Path $harmony)) { throw "Missing $harmony" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$refs = @(
    (Join-Path $managed "Assembly-CSharp.dll"),
    (Join-Path $managed "UnityEngine.CoreModule.dll"),
    (Join-Path $managed "UnityEngine.IMGUIModule.dll"),
    (Join-Path $managed "netstandard.dll"),
    (Join-Path $managed "mscorlib.dll"),
    (Join-Path $managed "System.dll"),
    (Join-Path $managed "System.Core.dll"),
    $harmony
)

foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "Missing reference assembly: $r" }
}

$refArg = "/reference:" + ($refs -join ",")

& $csc /target:library /platform:x64 /nologo /noconfig /nostdlib+ /out:$out $refArg $src

if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Copy-Item -Force $harmony $outDir

Write-Host "Built $out"

if ($Deploy) {
    $modDir = Join-Path $RimWorldDir "Mods\DisplayOptions"
    $modAbout = Join-Path $modDir "About"
    $modAssemblies = Join-Path $modDir "Assemblies"
    New-Item -ItemType Directory -Force -Path $modAbout, $modAssemblies | Out-Null
    Copy-Item -Force (Join-Path $root "About\About.xml") (Join-Path $modAbout "About.xml")
    Copy-Item -Force (Join-Path $root "About\Preview.png") (Join-Path $modAbout "Preview.png")
    Copy-Item -Force (Join-Path $root "About\PublishedFileId.txt") (Join-Path $modAbout "PublishedFileId.txt")
    Copy-Item -Force (Join-Path $outDir "*.dll") $modAssemblies
    Write-Host "Deployed to $modDir"
}
