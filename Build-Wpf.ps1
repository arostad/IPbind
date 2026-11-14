<#
    Build-Wpf.ps1  -  compiles the WPF Beta UI (in-box csc.exe, no SDK).
    --------------------------------------------------------------
    Shares Version.cs generation with Build-Exe.ps1 (reads $Version from it).
    Default channel is beta. Output: dist-wpf\IPbind WPF Beta.exe

    Usage:   .\Build-Wpf.ps1
             .\Build-Wpf.ps1 -Channel beta
#>

param(
    [ValidateSet('prod', 'beta')]
    [string]$Channel = 'beta'
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src  = Join-Path $here 'IPbind.cs'
$man  = Join-Path $here 'app.wpf.manifest'
$ico  = Join-Path $here ($(if ($Channel -eq 'beta') { 'app-beta.ico' } else { 'app.ico' }))
$ver  = Join-Path $here 'Version.cs'
$wpfDir = Join-Path $here 'Wpf'
$outDir = Join-Path $here 'dist-wpf'

if (-not (Test-Path $src)) { Write-Error "Missing $src"; return }
if (-not (Test-Path $man)) { Write-Error "Missing $man"; return }
if (-not (Test-Path $wpfDir)) { Write-Error "Missing $wpfDir"; return }

# --- keep Version.cs in lockstep with Build-Exe.ps1 ---
$buildExe = Get-Content (Join-Path $here 'Build-Exe.ps1') -Raw
if ($buildExe -notmatch '(?m)^\$Version = ''([^'']+)''') {
    Write-Error 'Could not read $Version from Build-Exe.ps1'
    return
}
$Version = $Matches[1]
$numeric = "$Version.0"
if ($Channel -eq 'beta') {
    $appName = 'IPbind Beta'
    $releaseTag = 'beta'
    $outName = 'IPbind WPF Beta.exe'
} else {
    $appName = 'IPbind'
    $releaseTag = 'latest'
    $outName = 'IPbind WPF.exe'
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

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$out = Join-Path $outDir $outName
if (Test-Path $out) {
    try {
        Remove-Item $out -Force -ErrorAction Stop
    } catch {
        $out = Join-Path $outDir ("IPbind WPF-{0}-{1}.exe" -f $Version, (Get-Date -Format 'HHmmss'))
        Write-Warning 'Output exe is LOCKED and could not be replaced.'
        Write-Warning ("Building to a fresh file instead:  {0}" -f (Split-Path $out -Leaf))
    }
}

$rt = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$csc = Join-Path $rt 'csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64','C:\Windows\Microsoft.NET\Framework' -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
           Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $csc -or -not (Test-Path $csc)) { Write-Error 'csc.exe not found (.NET Framework not present?)'; return }
Write-Host "Using compiler: $csc" -ForegroundColor Cyan

$wpfRt = Join-Path $rt 'WPF'
$wpfCs = @(Get-ChildItem $wpfDir -Filter '*.cs' | ForEach-Object { $_.FullName })
if ($wpfCs.Count -lt 1) { Write-Error "No Wpf\\*.cs sources in $wpfDir"; return }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/main:IPbind.WpfProgram'
    "/out:`"$out`""
    "/win32manifest:$man"
    '/optimize+'
    '/warnaserror+'
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Net.Http.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Xml.dll'
    (Join-Path $rt 'System.Xaml.dll' | ForEach-Object { "/r:$_" })
    (Join-Path $wpfRt 'WindowsBase.dll' | ForEach-Object { "/r:$_" })
    (Join-Path $wpfRt 'PresentationCore.dll' | ForEach-Object { "/r:$_" })
    (Join-Path $wpfRt 'PresentationFramework.dll' | ForEach-Object { "/r:$_" })
    $src
    $ver
)
$cscArgs += $wpfCs

$xaml = @(
    @{ file = 'MainWindow.xaml'; name = 'IPbind.Wpf.MainWindow.xaml' }
    @{ file = 'AboutWindow.xaml'; name = 'IPbind.Wpf.AboutWindow.xaml' }
    @{ file = 'DhcpReminderWindow.xaml'; name = 'IPbind.Wpf.DhcpReminderWindow.xaml' }
    @{ file = 'PrivacyConsentWindow.xaml'; name = 'IPbind.Wpf.PrivacyConsentWindow.xaml' }
    @{ file = 'Theme.xaml'; name = 'IPbind.Wpf.Theme.xaml' }
)
foreach ($x in $xaml) {
    $path = Join-Path $wpfDir $x.file
    if (-not (Test-Path $path)) { Write-Error "Missing $path"; return }
    $cscArgs = @("/resource:$path,$($x.name)") + $cscArgs
}

if (Test-Path $ico) { $cscArgs = @("/win32icon:$ico", "/resource:$ico,IPbind.app.ico") + $cscArgs }
else { Write-Warning 'app.ico not found in this folder - the exe (and its window/taskbar icon) will use the default icon.' }
$privacy = Join-Path $here 'installer\privacy.txt'
if (-not (Test-Path $privacy)) { Write-Error "Missing $privacy"; return }
$cscArgs = @("/resource:$privacy,IPbind.privacy.txt") + $cscArgs

Write-Host 'Compiling WPF ...' -ForegroundColor Cyan
& $csc @cscArgs

if (Test-Path $out) {
    $fi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($out)
    Write-Host ""
    Write-Host ("Done:  {0}" -f $out) -ForegroundColor Green
    Write-Host ("The finished file reports version: {0}" -f $fi.ProductVersion) -ForegroundColor Green
} else {
    Write-Error 'Build did not produce an exe - check the compiler output above (and the lock warning, if any).'
}
