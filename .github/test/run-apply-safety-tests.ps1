$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path .\Version.cs)) {
  powershell -ExecutionPolicy Bypass -File .\Build-Exe.ps1 -Channel beta
}
$src = Get-Content -Raw .\IPbind.cs
# MainForm is a non-public class; the test lives in another namespace in the same assembly.
$cargs = @(
  '/nologo', '/target:exe', '/out:apply-safety-tests.exe',
  '/main:IPbindTest.ApplySafetyTest',
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
  '/r:System.Net.Http.dll', '/r:System.Windows.Forms.dll',
  'IPbind.cs', 'Version.cs', '.github\test\ApplySafetyTest.cs'
)
& $csc @cargs
if ($LASTEXITCODE -ne 0) { throw 'Apply safety test compile failed' }
& .\apply-safety-tests.exe
if ($LASTEXITCODE -ne 0) { throw 'Apply safety tests failed' }
