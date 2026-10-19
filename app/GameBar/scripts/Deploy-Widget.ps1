# One-command deployment of the Game Bar widget to this machine.
#
# The parts that need administrator rights are done here (trusting the signing certificate
# machine-wide, and the sideloading policy); everything else is delegated to
# Pack-Widget.ps1, which is the single place that knows how to lay the package out.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File app\GameBar\scripts\Deploy-Widget.ps1
#
# Options:
#   -Configuration Debug|Release   (default Debug)
#   -Pfx / -Password               certificate to sign with (default: a dev certificate is
#                                  created in %TEMP%\ghelper-gamebar-deploy)

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Pfx,
    [string]$Password = 'ghelper'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workDir = Join-Path $env:TEMP 'ghelper-gamebar-deploy'
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Re-launching elevated: the signing certificate has to be trusted machine-wide." -ForegroundColor Yellow
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Configuration', $Configuration)
    if ($Pfx) { $args += @('-Pfx', $Pfx) }
    $args += @('-Password', $Password)

    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -Verb RunAs -PassThru -Wait
    exit $p.ExitCode
}

# ── Certificate ────────────────────────────────────────────────────────
# Windows will only install a package signed by a certificate trusted on the machine.
# certutil -f matters: without it, adding to the Root store pops a modal warning that
# blocks the command.
if (-not $Pfx) {
    $Pfx = Join-Path $workDir 'GHelperGameBar.pfx'
}

if (-not (Test-Path $Pfx)) {
    Write-Host 'Creating a development signing certificate'
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=G-Helper' `
        -KeyUsage DigitalSignature -FriendlyName 'G-Helper Game Bar' `
        -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}CA=false')

    $cer = Join-Path $workDir 'GHelperGameBar.cer'
    Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cer -Force | Out-Null
    Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $Pfx `
        -Password (ConvertTo-SecureString $Password -AsPlainText -Force) | Out-Null
}

$cer = Join-Path $workDir 'GHelperGameBar.cer'
if (Test-Path $cer) {
    Write-Host 'Trusting the certificate machine-wide'
    & certutil.exe -addstore -f TrustedPeople $cer | Out-Null
}

# ── Sideloading policy ─────────────────────────────────────────────────
# Loose deployment and unsigned packages need one of these; both are cheap to set and
# are what lets a sideloaded widget install at all.
foreach ($entry in @(
    @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'; Names = @('AllowAllTrustedApps', 'AllowDeveloperMode') },
    @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx';                  Names = @('AllowAllTrustedApps', 'AllowDevelopmentWithoutDevLicense') }
)) {
    if (-not (Test-Path $entry.Path)) { New-Item -Path $entry.Path -Force | Out-Null }
    foreach ($name in $entry.Names) {
        New-ItemProperty -Path $entry.Path -Name $name -Value 1 -PropertyType DWord -Force | Out-Null
    }
}
Write-Host 'Sideloading policy enabled'

# ── Build, pack, sign, install ─────────────────────────────────────────
& (Join-Path $PSScriptRoot 'Pack-Widget.ps1') -Configuration $Configuration -Sign -Pfx $Pfx -Password $Password -Install
exit $LASTEXITCODE
