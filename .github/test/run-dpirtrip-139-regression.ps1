# Rebuild uishot with the 2.3.139 GETDPISCALEDSIZE / ApplyDpi behavior and
# require that dpirtrip still fails (layout / control-bound drift, Andy's bug).
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$src = Join-Path $root 'IPbind.cs'
$tmp = Join-Path $root 'IPbind-139-regress.cs'
$code = [IO.File]::ReadAllText($src)
# Restore 139 GETDPISCALEDSIZE: layout at the new scale against the current
# (old-monitor) work area, only when sc changes.
$getOld = @'
                    deferResize = true;
                    // This message arrives before the window moves, so the
                    // current work area is still the old monitor. Do not widen
                    // or tighten against that; ApplyDpi re-lays out against the
                    // destination work area once WM_DPICHANGED supplies it.
                    layoutWork = new Rectangle(0, 0, 16384, 16384);
                    try
                    {
                        sc = dpi / 96f;
                        LayoutContent();
                    }
                    finally
                    {
                        layoutWork = Rectangle.Empty;
                        deferResize = false;
                        ResumeLayout(false);
                    }
'@
$get139 = @'
                    deferResize = true;
                    try
                    {
                        if (Math.Abs(dpi / 96f - sc) > 0.001f)
                        {
                            sc = dpi / 96f;
                            LayoutContent();
                        }
                    }
                    finally
                    {
                        deferResize = false;
                        ResumeLayout(false);
                    }
'@
if ($code.IndexOf($getOld) -lt 0) { throw 'GETDPISCALEDSIZE 140 block not found; cannot build 139 regression' }
$code = $code.Replace($getOld, $get139)
$appOld = @'
                    try
                    {
                        // Always re-lay out. WM_GETDPISCALEDSIZE may already have
                        // set sc and laid out against the old monitor's work area;
                        // skipping here left the popup far too wide after a
                        // 100% -> high-scale return trip (Andy's 2.3.139 catch).
                        sc = newSc;
                        LayoutContent();
                        relayout = false;
                    }
'@
$app139 = @'
                    try
                    {
                        if (relayout || Math.Abs(newSc - sc) > 0.001f)
                        {
                            sc = newSc;
                            LayoutContent();
                        }
                        relayout = false;
                    }
'@
if ($code.IndexOf($appOld) -lt 0) { throw 'ApplyDpi 140 always-relayout block not found; cannot build 139 regression' }
$code = $code.Replace($appOld, $app139)
[IO.File]::WriteAllText($tmp, $code)
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'uishot-139-regress.exe'
# asInvoker-style compile: reuse app.manifest like CI uishot (runners are admin).
$cargs = @(
  '/nologo','/warnaserror+','/target:winexe','/win32manifest:app.manifest','/win32icon:app-beta.ico',
  '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Net.Http.dll',
  '/r:System.Windows.Forms.dll','/main:IPbindTest.UiShot',
  '/resource:installer\privacy.txt,IPbind.privacy.txt',
  '/resource:app-beta.ico,IPbind.app.ico',"/out:$out"
)
& $csc @cargs $tmp Version.cs .github\test\UiShot.cs .github\test\ScaleControl.cs
if ($LASTEXITCODE -ne 0) { throw 'uishot-139-regress compile failed' }
$dir = Join-Path $root 'shots\dpirtrip-139'
$report = Join-Path $root 'shots\dpirtrip-139.txt'
New-Item -ItemType Directory -Force $dir, (Join-Path $root 'shots') | Out-Null
$p = Start-Process $out -ArgumentList @('dpirtrip',$dir,$report) -PassThru -Wait
if (Test-Path $report) { Get-Content $report }
$log = if (Test-Path $report) { Get-Content $report -Raw } else { '' }
$drift = $log -match 'client size drifted' -or $log -match 'FAIL was'
$faults = 0
if ($log -match 'FAULTS=(\d+)') { $faults = [int]$Matches[1] }
if ($p.ExitCode -eq 0 -or $faults -eq 0 -or -not $drift) {
  throw "dpirtrip 139 regression unexpectedly passed (exit $($p.ExitCode) FAULTS=$faults drift=$drift). Andy's return-trip bug must still fail."
}
Write-Host "OK: 139 dpirtrip regression failed as required (exit $($p.ExitCode) FAULTS=$faults)"
