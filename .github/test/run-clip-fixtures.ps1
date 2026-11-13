# The detector must fail on Andy's known-bad 2.3.139 Finished-page shot.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$shot = Join-Path $here 'fixtures\inno-139-finish-clipped.png'
$log = Join-Path $PWD 'shots\clip-fixture-139-finish.txt'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
if (Test-Path $log) { Remove-Item $log -Force }
$p = Start-Process powershell -ArgumentList @('-ExecutionPolicy','Bypass','-File',(Join-Path $here 'check-inno-checkbox-clip.ps1'),'-Path',$shot,'-Log',$log) -PassThru -Wait
if (Test-Path $log) { Get-Content $log }
if ($p.ExitCode -lt 1) {
  throw "clip detector exited $($p.ExitCode) on the 2.3.139 Finished-page fixture; it must report a clipped checkbox"
}
Write-Host "OK: 139 Finished-page fixture failed the clip check (exit $($p.ExitCode))"
