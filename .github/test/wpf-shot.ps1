# WPF Beta real-launch check: start the exe, find its main window, capture it
# from the screen, and fail if the window is blank, off the work area, or the
# main controls are missing. Optional -Trip changes the display scale while the
# app runs (via uishot.exe setscale) and re-checks after each change.
param(
  [string]$Exe, [string]$Out, [string]$Tag,
  [string]$Theme = '',
  [string[]]$Trip = @(),
  [string]$UiShot = '.\uishot.exe'
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class WS {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, int f);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MI i);
  public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int cb; public RECT mon; public RECT work; public int flags; }
}
'@
[WS]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out "wpf-shot-$Tag.txt"
$fails = 0
function L([string]$s) { $line = "[$((Get-Date).ToString('HH:mm:ss.fff'))] $s"; Add-Content $log $line; Write-Host $line }
function F([string]$s) { $script:fails++; L "FAIL $s" }
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Check([IntPtr]$h, [string]$name) {
  $r = New-Object WS+RECT
  if ([WS]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { F "$name no window rect"; return }
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  $mi = New-Object WS+MI; $mi.cb = [System.Runtime.InteropServices.Marshal]::SizeOf($mi)
  [WS]::GetMonitorInfo([WS]::MonitorFromWindow($h, 2), [ref]$mi) | Out-Null
  $wa = $mi.work
  L "$name window ${w}x${hh} at $($r.L),$($r.T); work area $($wa.L),$($wa.T)-$($wa.R),$($wa.B)"
  if ($w -lt 200 -or $hh -lt 150) { F "$name window too small" }
  if ($r.L -lt $wa.L - 8 -or $r.T -lt $wa.T - 8 -or $r.R -gt $wa.R + 8 -or $r.B -gt $wa.B + 8) { F "$name window is outside the work area" }
  $bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1,$w)), ([Math]::Max(1,$hh))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
  $png = Join-Path $Out "wpf-$Tag-$name.png"; $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
  # Blank check: sample a grid; a rendered window has many distinct colors.
  $seen = @{}
  for ($y = 40; $y -lt $hh; $y += [Math]::Max(1, [int]($hh / 60))) {
    for ($x = 0; $x -lt $w; $x += [Math]::Max(1, [int]($w / 80))) { $seen[$bmp.GetPixel($x, $y).ToArgb()] = 1 }
  }
  $bmp.Dispose()
  L "$name distinct sampled colors: $($seen.Count)"
  if ($seen.Count -lt 12) { F "$name window looks blank ($($seen.Count) colors)" }
  $root = $AE::FromHandle($h)
  foreach ($n in @('Apply Static IPs', 'Return to DHCP', 'Refresh', 'About', 'Clear')) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)
    $el = $root.FindFirst($TS::Descendants, $c)
    if (-not $el) { F "$name control '$n' not found"; continue }
    $b = $el.Current.BoundingRectangle
    if ($el.Current.IsOffscreen -or $b.Width -lt 4 -or $b.Left -lt $r.L - 2 -or $b.Right -gt $r.R + 2 -or $b.Bottom -gt $r.B + 2) {
      F "$name control '$n' off-screen or clipped ($($b.Left),$($b.Top) $($b.Width)x$($b.Height))"
    }
  }
}

$args2 = @(); if ($Theme) { $args2 += @('--theme', $Theme) }
$p = if ($args2.Count) { Start-Process $Exe -ArgumentList $args2 -PassThru } else { Start-Process $Exe -PassThru }
$h = [IntPtr]::Zero
$end = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $end) {
  Start-Sleep -Milliseconds 500
  $p.Refresh()
  if ($p.HasExited) { break }
  if ($p.MainWindowHandle -ne [IntPtr]::Zero -and $p.MainWindowTitle -like 'IPbind Beta v*') { $h = $p.MainWindowHandle; break }
}
if ($h -eq [IntPtr]::Zero) { F "main window not found (title '$($p.MainWindowTitle)', exited $($p.HasExited))"; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
Start-Sleep -Seconds 4
Check $h 'start'
foreach ($pct in $Trip) {
  $txtPath = Join-Path $Out "setscale-$Tag-$pct.txt"
  Start-Process $UiShot -ArgumentList @('setscale', $pct, $txtPath) -Wait | Out-Null
  Start-Sleep -Seconds 5
  $st = if (Test-Path $txtPath) { (Get-Content $txtPath -Raw).Trim() } else { '' }
  L "setscale $pct -> $st"
  if ($st -match 'UNAVAILABLE') { F "setscale $pct unavailable"; continue }
  $p.Refresh()
  if ($p.HasExited) { F "app exited after scale change to $pct"; break }
  Check $p.MainWindowHandle "at$pct"
}
if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep 2; if (-not $p.HasExited) { $p.Kill() } }
L "fails=$fails"
if ($fails -gt 0) { exit 1 }
exit 0
