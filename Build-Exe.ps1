<#
    Build-Exe.ps1  -  compiles IPbind.cs into IPbind.exe (in-box csc.exe).
    --------------------------------------------------------------
    No ps2exe, no SDK, no internet. Result is a normal compiled .NET
    WinForms binary.

    Needs in the same folder:  IPbind.cs, app.manifest, app.ico

    VERSIONING (conventional - bump $Version by hand):
        Patch  : tiny change      2.1.101 -> 2.1.102 -> 2.1.103 ...
        Minor  : bigger change    2.1.x   -> 2.2.101 (reset patch to 101)
        Major  : reserved for large overhauls
      The value below is stamped into Version.cs (assembly metadata and
      BuildInfo.Version). Beta UI/log lines show it with a trailing "b"
      (DisplayVersion); assembly, file, and installer versions stay numeric.

    LOCK-PROOF: if IPbind.exe is locked (antivirus or a running instance)
    the compiler cannot overwrite it, so this script deletes it first and,
    if it can't, builds to a uniquely-named file so you always get a fresh
    binary to run.

    CHANNEL: -Channel prod (default) builds IPbind. -Channel beta builds the
    separate "IPbind Beta" app (own name, dark-gray and amber icon app-beta.ico, own
    settings folder, updates only from the beta pre-release). CI uses beta
    only for the beta branch.

    Usage:   .\Build-Exe.ps1
             .\Build-Exe.ps1 -Channel beta
#>

param(
    [ValidateSet('prod', 'beta')]
    [string]$Channel = 'prod'
)

# ==============================================================
$Version = '2.3.133'     # <-- BUMP THIS each change (see scheme above)
# ==============================================================

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src  = Join-Path $here 'IPbind.cs'
$man  = Join-Path $here 'app.manifest'
$ico  = Join-Path $here ($(if ($Channel -eq 'beta') { 'app-beta.ico' } else { 'app.ico' }))
$ver  = Join-Path $here 'Version.cs'
$out  = Join-Path $here 'IPbind.exe'

if (-not (Test-Path $src)) { Write-Error "Missing $src"; return }
if (-not (Test-Path $man)) { Write-Error "Missing $man"; return }

# --- generate Version.cs from $Version ---
$numeric = "$Version.0"   # 4-part numeric file version, e.g. 2.1.101.0
if ($Channel -eq 'beta') {
    $appName = 'IPbind Beta'
    $releaseTag = 'beta'
} else {
    $appName = 'IPbind'
    $releaseTag = 'latest'
}
$assemblyTitle = "$appName - Static IP binder for easily accessing air-gapped equipment across multiple IP ranges"
$verSource = @"
using System.Reflection;
[assembly: AssemblyTitle("$assemblyTitle")]
[assembly: AssemblyProduct("$appName")]
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$Version")]
namespace IPbind
{
    internal static class BuildInfo
    {
        public const string Version = "$Version";
        public const string Channel = "$Channel";
        public static readonly bool IsBeta = Channel == "beta";
        // Shown in the title bar, About, footer, and log. Beta appends "b"
        // (e.g. 2.3.132b). Parsed versions stay the numeric Version above.
        public static readonly string DisplayVersion = IsBeta ? Version + "b" : Version;
        // Window/About name and the %APPDATA% / %LOCALAPPDATA% folder name.
        public const string AppName = "$appName";
        // GitHub release tag the updater reads from (version.txt, IPbind.exe, .sha256).
        public const string ReleaseTag = "$releaseTag";
    }
}
"@
Set-Content -Path $ver -Value $verSource -Encoding UTF8
Write-Host "Build version: $Version  (file version $numeric), channel: $Channel ($appName)" -ForegroundColor Cyan

# --- never silently keep a locked old exe ---
if (Test-Path $out) {
    try {
        Remove-Item $out -Force -ErrorAction Stop
    } catch {
        $out = Join-Path $here ("IPbind-{0}-{1}.exe" -f $Version, (Get-Date -Format 'HHmmss'))
        Write-Warning 'IPbind.exe is LOCKED (antivirus or a running instance) and could not be replaced.'
        Write-Warning ("Building to a fresh file instead:  {0}" -f (Split-Path $out -Leaf))
        Write-Warning 'Run THAT file. To fix the lock: close any running IPbind, check Bitdefender quarantine/exclusions, or reboot.'
    }
}

# --- locate csc.exe from the installed .NET Framework (no SDK needed) ---
$csc = Join-Path ([System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64','C:\Windows\Microsoft.NET\Framework' -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
           Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $csc -or -not (Test-Path $csc)) { Write-Error 'csc.exe not found (.NET Framework not present?)'; return }
Write-Host "Using compiler: $csc" -ForegroundColor Cyan

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    "/out:$out"
    "/win32manifest:$man"
    '/optimize+'
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Net.Http.dll'
    '/r:System.Windows.Forms.dll'
    $src
    $ver
)
if (Test-Path $ico) { $cscArgs = @("/win32icon:$ico", "/resource:$ico,IPbind.app.ico") + $cscArgs }
else { Write-Warning 'app.ico not found in this folder - the exe (and its window/taskbar icon) will use the default icon.' }
# Embedded so About's consent dialog shows exactly the installer's privacy page.
$privacy = Join-Path $here 'installer\privacy.txt'
if (-not (Test-Path $privacy)) { Write-Error "Missing $privacy"; return }
$cscArgs = @("/resource:$privacy,IPbind.privacy.txt") + $cscArgs

Write-Host 'Compiling ...' -ForegroundColor Cyan
& $csc @cscArgs

if (Test-Path $out) {
    $fi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($out)
    Write-Host ""
    Write-Host ("Done:  {0}" -f $out) -ForegroundColor Green
    Write-Host ("The finished file reports version: {0}" -f $fi.ProductVersion) -ForegroundColor Green
    Write-Host ("Open it and confirm the title bar shows v{0}." -f ($(if ($Channel -eq 'beta') { "$Version" + 'b' } else { $Version }))) -ForegroundColor Green
} else {
    Write-Error 'Build did not produce an exe - check the compiler output above (and the lock warning, if any).'
}
