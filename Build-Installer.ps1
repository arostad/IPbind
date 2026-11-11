<#
    Builds the optional per-user IPbind installer with Inno Setup 6.

    By default this script builds IPbind.exe first. Pass -ExePath to package
    an existing executable instead.

    Usage:
      .\Build-Installer.ps1
      .\Build-Installer.ps1 -ExePath .\IPbind.exe
      .\Build-Installer.ps1 -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
      .\Build-Installer.ps1 -Channel beta -ExePath .\IPbind.exe   (exe built with -Channel beta)

    -Channel beta packages the separate "IPbind Beta" app as
    IPbind-Installer-v<version>-beta.exe (own AppId, folder and shortcuts).
#>

[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$IsccPath,
    [string]$OutputDirectory,
    [ValidateSet('prod', 'beta')]
    [string]$Channel = 'prod'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    Write-Host 'Building IPbind.exe ...' -ForegroundColor Cyan
    & (Join-Path $root 'Build-Exe.ps1') -Channel $Channel
    if ($LASTEXITCODE -ne 0) {
        throw "Build-Exe.ps1 failed with exit code $LASTEXITCODE."
    }
    $ExePath = Join-Path $root 'IPbind.exe'
}

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExe)
$version = $fileInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = $fileInfo.FileVersion
}
$version = ($version -split '\+')[0].Trim()
if ($version -notmatch '^\d+(?:\.\d+){2,3}$') {
    throw "The executable reports an unsupported product version: '$version'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'dist'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        $IsccPath = $command.Source
    } else {
        $candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        )
        $IsccPath = $candidates |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
            Select-Object -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or
    -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath.'
}
$resolvedIscc = (Resolve-Path -LiteralPath $IsccPath).Path
$installerScript = Join-Path $root 'installer\IPbind.iss'

Write-Host "Packaging IPbind $version with $resolvedIscc ..." -ForegroundColor Cyan
& $resolvedIscc `
    "/DAppVersion=$version" `
    "/DExePath=$resolvedExe" `
    "/DOutputDir=$resolvedOutput" `
    "/DChannel=$Channel" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerName = if ($Channel -eq 'beta') { "IPbind-Installer-v$version-beta.exe" } else { "IPbind-Installer-v$version.exe" }
$installerPath = Join-Path $resolvedOutput $installerName
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Inno Setup did not produce the expected installer: $installerPath"
}

# Legacy unversioned name (prod only): not linked anywhere new, but kept on the
# Latest release so older README copies and bookmarks still download the current build.
$stableInstallerPath = $null
if ($Channel -eq 'prod') {
    $stableInstallerPath = Join-Path $resolvedOutput 'IPbind-Setup.exe'
    Copy-Item -LiteralPath $installerPath -Destination $stableInstallerPath -Force
}

Write-Host "Done: $installerPath" -ForegroundColor Green
if ($stableInstallerPath) { Write-Host "Legacy copy: $stableInstallerPath" -ForegroundColor Green }
[PSCustomObject]@{
    Version = $version
    InstallerPath = $installerPath
    StableInstallerPath = $stableInstallerPath
}
