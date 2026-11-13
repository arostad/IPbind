# Throwaway 2.3.135 real-user walkthrough: install (every page), first launch and
# the setup reminder, main window, About, then uninstall paths. Screenshots + log.
param([string]$Installer, [string]$Out, [string]$Tag)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@
[W]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out "walk-$Tag.txt"
function L([string]$s) { $line = "[$((Get-Date).ToString('HH:mm:ss.fff'))] $s"; Add-Content $log $line; Write-Host $line }
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function FindWin([string]$name, [int]$sec = 15, [switch]$Like) {
  $end = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $end) {
    foreach ($w in $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
      $n = $w.Current.Name
      if (($Like -and $n -like $name) -or (-not $Like -and $n -eq $name)) { return $w }
    }
    Start-Sleep -Milliseconds 250
  }
  return $null
}
function Shot($win, [string]$name) {
  try {
    $h = [IntPtr]$win.Current.NativeWindowHandle
    $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $hh = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $bmp.Save((Join-Path $Out "$Tag-$name.png")); $g.Dispose(); $bmp.Dispose()
    L "shot $name ($w x $hh)"
  } catch { L "shot $name failed: $_" }
}
function ShotScreen([string]$name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
  $bmp.Save((Join-Path $Out "$Tag-$name.png")); $g.Dispose(); $bmp.Dispose(); L "screen $name"
}
function All($win) { $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
$CT = [System.Windows.Automation.ControlType]
# Inno's VCL controls show up as ControlType.Pane, so match on class name as well.
function OfKind($win, [string]$kind) {
  $type = @{ Button = $CT::Button; CheckBox = $CT::CheckBox; RadioButton = $CT::RadioButton }[$kind]
  @(All $win | Where-Object { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$kind" -or $_.Current.ClassName -eq "TNew$kind" })
}
function Btn($win, [string]$name) {
  foreach ($b in (OfKind $win 'Button')) {
    $n = ([string]$b.Current.Name).Replace('&', '').Trim()
    if ($n -eq $name -or $n -eq ($name -replace ' >$', '')) { return $b }
  }
  return $null
}
# BM_CLICK, posted so a modal opened by the click can't block the script.
function Click($el) {
  if (-not $el) { L 'click: no element'; return $false }
  $h = [IntPtr]$el.Current.NativeWindowHandle
  if ($h -eq [IntPtr]::Zero) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { L "click failed: $_"; return $false }
  }
  [W]::PostMessage($h, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; return $true
}
function Checks($win) { OfKind $win 'CheckBox' }
function Texts($win) {
  ($win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
}
$AppDir = Join-Path $env:LOCALAPPDATA 'Programs\IPbind Beta'
$Data = Join-Path $env:APPDATA 'IPbind Beta'
$Exe = Join-Path $AppDir 'IPbind Beta.exe'
$Key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{3E0BECC0-9CDB-47D6-BFFB-5A6B0DEEFC27}_is1'
function State([string]$when) { L "state $when : app=$(Test-Path $Exe) data=$(Test-Path $Data) key=$(Test-Path $Key)" }

Get-Process -ea SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins*' -or $_.ProcessName -eq 'IPbind Beta' } | Stop-Process -Force -ea SilentlyContinue
# ---------------- install, every page ----------------
L "installer: $Installer"
Start-Process $Installer
$wiz = FindWin 'Setup - IPbind Beta*' 30 -Like
if (-not $wiz) { L 'FAIL wizard window not found'; ShotScreen 'no-wizard'; exit 1 }
for ($i = 1; $i -le 12; $i++) {
  Start-Sleep -Milliseconds 1500
  Shot $wiz ("install-{0:D2}" -f $i)
  L "page $i texts: $(Texts $wiz)"
  $accept = OfKind $wiz 'RadioButton' | Where-Object { $_.Current.Name -like 'I accept*' } | Select-Object -First 1
  if ($accept) { L 'accept licence'; Click $accept | Out-Null; Start-Sleep -Milliseconds 400; Shot $wiz ("install-{0:D2}b" -f $i) }
  $fin = Btn $wiz 'Finish'
  if ($fin) { L 'finish page'; Click $fin | Out-Null; break }
  $next = Btn $wiz 'Next >'; if (-not $next -or -not $next.Current.IsEnabled) { $i2 = Btn $wiz 'Install'; if ($i2) { $next = $i2 } }
  if ($next) { L "click $($next.Current.Name)"; Click $next | Out-Null } else { L "no Next/Install button; buttons: $((OfKind $wiz 'Button' | ForEach-Object { $_.Current.Name + '/' + $_.Current.IsEnabled }) -join ', ')" }
}
State 'after install'
L "UninstallString: $((Get-ItemProperty $Key).UninstallString)"
L "QuietUninstallString: $((Get-ItemProperty $Key).QuietUninstallString)"

# ---------------- first launch (from Finish), reminder ----------------
$rem = FindWin 'IPbind Beta' 30
Start-Sleep -Seconds 3
ShotScreen 'first-launch-screen'
$rem = $null
foreach ($w in $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($w.Current.Name -eq 'IPbind Beta' -and (Checks $w).Count -ge 2 -and (Btn $w 'Continue')) { $rem = $w }
}
if ($rem) {
  Shot $rem 'reminder'
  L "reminder texts: $(Texts $rem)"
  $c = Checks $rem; Click $c[0] | Out-Null; Start-Sleep -Milliseconds 500; Shot $rem 'reminder-ack'
  Click (Btn $rem 'Continue') | Out-Null
} else { L 'WARN reminder not found' }
Start-Sleep -Seconds 2
$main = FindWin 'IPbind Beta v*' 15 -Like
if ($main) {
  Shot $main 'main'
  $gw = Checks $main | Where-Object { $_.Current.Name -eq 'Gateway support' }
  if ($gw) { Click $gw | Out-Null; Start-Sleep -Milliseconds 800; Shot $main 'main-gateway'; Click $gw | Out-Null }
  $about = Btn $main 'About'
  if ($about) {
    Click $about | Out-Null
    $ab = FindWin 'About IPbind Beta' 10
    if ($ab) { Start-Sleep -Seconds 2; Shot $ab 'about'; L "about texts: $(Texts $ab)"; Click (Btn $ab 'Close') | Out-Null }
  }
  Start-Sleep -Seconds 1
  Get-Process 'IPbind Beta' -ea SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
  Start-Sleep -Seconds 2
  # Exit may ask about keep-dhcp etc.
  ShotScreen 'after-close'
  Get-Process 'IPbind Beta' -ea SilentlyContinue | Stop-Process -Force
} else { L 'WARN main window not found'; ShotScreen 'no-main' }
New-Item -ItemType Directory -Force $Data | Out-Null
Set-Content (Join-Path $Data 'keep-dhcp-test.txt') 'test'

function UninstallInteractive([string]$name, [string]$how, [bool]$check, [string]$button) {
  State "before uninstall $name"
  $us = (Get-ItemProperty $Key).UninstallString
  if ($how -eq 'settings') { L "run UninstallString via cmd: $us"; Start-Process cmd.exe -ArgumentList '/c', "`"$us`"" -WindowStyle Hidden }
  else { $u = UninsExe; L "run $u (no args)"; Start-Process $u }
  $dlg = FindWin 'Uninstall IPbind Beta' 20
  if (-not $dlg) { L "FAIL $name prompt not found"; ShotScreen "$name-no-prompt"; return }
  foreach ($t in @(1, 4, 8)) {
    Start-Sleep -Seconds ($(if ($t -eq 1) { 1 } else { 4 }))
    $still = $null -ne (FindWin 'Uninstall IPbind Beta' 1)
    L "$name t~${t}s promptOpen=$still app=$(Test-Path $Exe) data=$(Test-Path $Data)"
    ShotScreen "$name-t$t"
  }
  Shot $dlg "$name-prompt"
  L "$name prompt texts: $(Texts $dlg)"
  # A stray Enter must not start the uninstall (Uninstall is not the default button).
  $c0 = @(Checks $dlg)[0]
  if ($c0) {
    $h0 = [IntPtr]$c0.Current.NativeWindowHandle
    [W]::PostMessage($h0, 0x100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null; [W]::PostMessage($h0, 0x101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 3
    L "$name after Enter: promptOpen=$($null -ne (FindWin 'Uninstall IPbind Beta' 1)) app=$(Test-Path $Exe) data=$(Test-Path $Data)"
  }
  if ($check) { $c = Checks $dlg; Click $c[0] | Out-Null; Start-Sleep -Milliseconds 400; Shot $dlg "$name-prompt-checked" }
  Click (Btn $dlg $button) | Out-Null
  $end = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $end -and (Get-Process -ea SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins*' })) {
    $msg = FindWin '*IPbind Beta*' 1 -Like
    Start-Sleep -Milliseconds 500
  }
  Start-Sleep -Seconds 2
  ShotScreen "$name-after"
  State "after uninstall $name"
  $left = @(Get-Process -ea SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins*' })
  if ($left.Count) { L "WARN leftover uninstaller processes: $($left.ProcessName -join ', ') - killing"; $left | Stop-Process -Force -ea SilentlyContinue; Start-Sleep 2 }
}
function UninsExe { $us = (Get-ItemProperty $Key -ea SilentlyContinue).UninstallString; if ($us -match '"([^"]+)"') { $Matches[1] } else { Join-Path $AppDir 'unins000.exe' } }
function Reinstall {
  Start-Process $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
  New-Item -ItemType Directory -Force $Data | Out-Null
  Set-Content (Join-Path $Data 'saved-list.txt') 'test'
  State 'reinstalled'
}
UninstallInteractive 'uninst-cancel' 'settings' $true 'Cancel'
UninstallInteractive 'uninst-keep' 'settings' $false 'Uninstall'
Reinstall
UninstallInteractive 'uninst-remove' 'direct' $true 'Uninstall'
Reinstall
L 'silent: /VERYSILENT (keeps data)'
Start-Process (UninsExe) -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES' -Wait; Start-Sleep 3; State 'after /VERYSILENT'
Reinstall
L 'silent: /VERYSILENT /REMOVEDATA=1'
Start-Process (UninsExe) -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/REMOVEDATA=1' -Wait; Start-Sleep 3; State 'after /VERYSILENT /REMOVEDATA=1'
L 'done'
