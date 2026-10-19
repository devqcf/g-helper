# Builds the .msix for the Game Bar widget, without Visual Studio.
#
# Why this exists
# ---------------
# A UWP project with the .NET Native toolchain produces its real payload in
# bin\<config>\ilc\ - the native bootstrapper, the .NET Native image, ucrtbased.dll and
# the assets. The *managed* build output next to it (the 48 KB exe plus ~170 reference
# assemblies) is not what ships.
#
# Visual Studio knows the difference because it reads the generated .appxrecipe, which
# lists every file and the path it takes inside the package. Packaging the output
# directory directly ships the managed app instead, and that app cannot load the Game
# Bar SDK: it dies on startup with BadImageFormatException.
#
# So: read the recipe, lay the files out exactly as it says, then pack that.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File app\GameBar\scripts\Pack-Widget.ps1
#   powershell -ExecutionPolicy Bypass -File app\GameBar\scripts\Pack-Widget.ps1 -Configuration Release
#
# Options:
#   -Configuration Debug|Release   (default Debug)
#   -OutputPath <path>             (default %TEMP%\ghelper-gamebar-deploy\GHelperGameBar.msix)
#   -Sign                          sign the package with a .pfx
#   -Pfx / -Password               certificate for signing
#   -Install                       install the package after building it

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$OutputPath,
    [switch]$Sign,
    [string]$Pfx,
    [string]$Password,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$gameBar  = Join-Path $repoRoot 'app\GameBar'
$buildDir = Join-Path $gameBar "bin\x64\$Configuration"

if (-not $OutputPath) {
    $OutputPath = Join-Path $env:TEMP 'ghelper-gamebar-deploy\GHelperGameBar.msix'
}
$workDir = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$recipe = Join-Path $buildDir 'GHelper.GameBar.build.appxrecipe'
if (-not (Test-Path $recipe)) {
    throw "No build recipe at $recipe - build the project first (msbuild /t:Rebuild)."
}

Write-Host "Reading $recipe" -ForegroundColor Cyan
[xml]$recipeXml = Get-Content $recipe -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($recipeXml.NameTable)
$ns.AddNamespace('b', 'http://schemas.microsoft.com/developer/msbuild/2003')

$manifest = $recipeXml.SelectSingleNode("//b:AppXManifest", $ns)
$files    = $recipeXml.SelectNodes("//b:AppxPackagedFile", $ns)

if (-not $manifest -or $files.Count -eq 0) {
    throw "The recipe does not list any payload files."
}

# A clean layout, then copy each file to the path the recipe gives it.
$layout = Join-Path $workDir "layout-$Configuration"
Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $layout | Out-Null

$copied = 0
$missing = @()

foreach ($file in $files) {
    $source = $file.Include
    $target = Join-Path $layout $file.PackagePath

    if (-not (Test-Path $source)) { $missing += $source; continue }

    $targetDir = Split-Path $target -Parent
    if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
    Copy-Item $source $target -Force
    $copied++
}

# The manifest itself.
$manifestTarget = Join-Path $layout 'AppxManifest.xml'
$manifestDir = Split-Path $manifestTarget -Parent
if (-not (Test-Path $manifestDir)) { New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null }
Copy-Item $manifest.Include $manifestTarget -Force

Write-Host "  laid out $copied files" -ForegroundColor Green
if ($missing.Count -gt 0) {
    Write-Host "  WARNING: $($missing.Count) file(s) listed in the recipe are missing:" -ForegroundColor Yellow
    $missing | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
}

# ── Pack ──────────────────────────────────────────────────────────────
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory |
    Where-Object { $_.Name -match '^\d+\.' } | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK not found.' }

$makeappx = Join-Path $sdk.FullName 'x64\makeappx.exe'
$signtool = Join-Path $sdk.FullName 'x64\signtool.exe'

Remove-Item $OutputPath -Force -ErrorAction SilentlyContinue
Write-Host "Packing $OutputPath" -ForegroundColor Cyan
& $makeappx pack /d $layout /p $OutputPath /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
Write-Host ("  {0:N2} MB" -f ((Get-Item $OutputPath).Length / 1MB)) -ForegroundColor Green

# ── Sign ──────────────────────────────────────────────────────────────
if ($Sign) {
    if (-not $Pfx) { throw '-Sign needs -Pfx.' }
    Write-Host 'Signing' -ForegroundColor Cyan
    & $signtool sign /fd SHA256 /a /f $Pfx /p $Password $OutputPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

# ── Install ───────────────────────────────────────────────────────────
if ($Install) {
    Write-Host 'Installing' -ForegroundColor Cyan
    Get-AppxPackage -Name 'GHelperGameBar*' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $OutputPath
    $pkg = Get-AppxPackage -Name 'GHelperGameBar*'
    Write-Host "  installed $($pkg.PackageFullName)" -ForegroundColor Green
}

Write-Host "Done: $OutputPath" -ForegroundColor Green
