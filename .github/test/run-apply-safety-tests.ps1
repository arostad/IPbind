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

$displayArgs = @(
  '/nologo', '/target:exe', '/out:adapter-display-tests.exe',
  '/main:IPbindTest.AdapterDisplayTest',
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
  '/r:System.Net.Http.dll', '/r:System.Windows.Forms.dll',
  'IPbind.cs', 'Version.cs', '.github\test\AdapterDisplayTest.cs'
)
& $csc @displayArgs
if ($LASTEXITCODE -ne 0) { throw 'Adapter display test compile failed' }
& .\adapter-display-tests.exe
if ($LASTEXITCODE -ne 0) { throw 'Adapter display tests failed' }

# Prod-channel compile of the same sources: DebugLog must create no files or folders.
$prodVer = @'
using System.Reflection;
[assembly: AssemblyTitle("IPbind")]
[assembly: AssemblyProduct("IPbind")]
[assembly: AssemblyVersion("2.3.138")]
[assembly: AssemblyFileVersion("2.3.138")]
[assembly: AssemblyInformationalVersion("2.3.138")]
namespace IPbind
{
    internal static class BuildInfo
    {
        public const string Version = "2.3.138";
        public const string Channel = "prod";
        public static readonly bool IsBeta = Channel == "beta";
        public static readonly string DisplayVersion = IsBeta ? Version + "b" : Version;
        public const string AppName = "IPbind";
        public const string ReleaseTag = "latest";
    }
}
'@
Set-Content -Path .\Version-prod-test.cs -Value $prodVer -Encoding UTF8
$logArgs = @(
  '/nologo', '/target:exe', '/out:debug-log-prod-tests.exe',
  '/main:IPbindTest.DebugLogTest',
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
  '/r:System.Net.Http.dll', '/r:System.Windows.Forms.dll',
  'IPbind.cs', 'Version-prod-test.cs', '.github\test\DebugLogTest.cs'
)
& $csc @logArgs
if ($LASTEXITCODE -ne 0) { throw 'Prod debug-log test compile failed' }
& .\debug-log-prod-tests.exe
if ($LASTEXITCODE -ne 0) { throw 'Prod debug-log test failed' }
