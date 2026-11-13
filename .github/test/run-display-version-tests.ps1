$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

$failures = @()

function Fail([string]$msg) { $script:failures += $msg; Write-Host "FAIL: $msg" }

# --- Source grep: every user-visible version in Beta must use DisplayVersion / VersionText.ForDisplay / AppDisplayVersion.
# Comparisons, version.txt, filenames, and file-version stamps stay numeric (BuildInfo.Version / AppVersion).
$cs = Get-Content -Raw .\IPbind.cs
$iss = Get-Content -Raw .\installer\IPbind.iss
$buildInst = Get-Content -Raw .\Build-Installer.ps1

if ($cs -notmatch 'class VersionText') {
  Fail 'IPbind.cs is missing VersionText (ForDisplay / Numeric / TryParse) so Beta can show a trailing b without breaking numeric compares.'
}
if ($cs -match 'SetValue\(\s*"DisplayVersion"\s*,\s*BuildInfo\.Version') {
  Fail 'InstalledVersionSync writes BuildInfo.Version (numeric) to Apps & Features DisplayVersion; Beta must write BuildInfo.DisplayVersion (e.g. 2.3.139b).'
}
if ($cs -match '"Version "\s*\+\s*info\.RemoteVersion') {
  Fail 'Update-available copy uses raw RemoteVersion (numeric). Beta must show VersionText.ForDisplay(info.RemoteVersion) (e.g. 2.3.140b is available).'
}
if ($iss -match 'AppVerName=\{#AppTitle\} \{#AppVersion\}') {
  Fail 'Inno AppVerName uses numeric AppVersion, so the installer wizard shows 2.3.139 instead of 2.3.139b on Beta.'
}
if ($iss -notmatch 'AppDisplayVersion') {
  Fail 'installer/IPbind.iss has no AppDisplayVersion (wizard + Apps & Features DisplayVersion should use it on Beta; AppVersion / VersionInfo* stay numeric).'
}
if ($buildInst -notmatch 'AppDisplayVersion') {
  Fail 'Build-Installer.ps1 does not pass /DAppDisplayVersion= (Beta should pass Version + b; prod equals numeric Version).'
}

# --- Compile and run the unit tests against the Beta Version.cs (and a prod stub).
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path .\Version.cs)) {
  powershell -ExecutionPolicy Bypass -File .\Build-Exe.ps1 -Channel beta
}

$testArgs = @(
  '/nologo', '/target:exe', '/out:display-version-tests.exe',
  '/main:IPbindTest.DisplayVersionTest',
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
  '/r:System.Net.Http.dll', '/r:System.Windows.Forms.dll',
  'IPbind.cs', 'Version.cs', '.github\test\DisplayVersionTest.cs'
)
& $csc @testArgs
if ($LASTEXITCODE -ne 0) {
  Fail 'DisplayVersionTest did not compile (VersionText / InstalledVersionSync.ShouldWrite are missing or the test is wrong).'
} else {
  & .\display-version-tests.exe
  if ($LASTEXITCODE -ne 0) { Fail 'DisplayVersionTest failed.' }
}

if ($failures.Count -gt 0) {
  Write-Host ("DISPLAY_VERSION_FAILS={0}" -f $failures.Count)
  throw ("Display version checks failed: {0}" -f ($failures -join ' / '))
}
Write-Host 'DISPLAY_VERSION_FAILS=0'
