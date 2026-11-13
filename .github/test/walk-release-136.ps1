# Real-user walkthrough of the PUBLISHED Beta 2.3.136 installer:
# install 2.3.135, install 136 over it, check Installed apps, launch, About and
# the DHCP popup at 100/150/175 and after a live scale change, open both
# dropdowns, in-app update from 135, uninstall with the remove-data step.
param(
  [Parameter(Mandatory=$true)][string]$Installer135,
  [Parameter(Mandatory=$true)][string]$Installer136,
  [Parameter(Mandatory=$true)][string]$Out,
  [Parameter(Mandatory=$true)][string]$SetScale
)
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
$log = Join-Path $Out 'walk-release-136.txt'
$fails = 0
function L([string]$s) { $line = "[$((Get-Date).ToString('HH:mm:ss.fff'))] $s"; Add-Content $log $line; Write-Host $line }
function Fail([string]$s) { $script:fails++; L "FAIL $s" }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
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
    if ($w -lt 2 -or $hh -lt 2) { L "shot $name skipped (size $w x $hh)"; return }
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $bmp.Save((Join-Path $Out "$name.png")); $g.Dispose(); $bmp.Dispose()
    L "shot $name ($w x $hh)"
  } catch { L "shot $name failed: $_" }
}
function ShotScreen([string]$name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
  $bmp.Save((Join-Path $Out "$name.png")); $g.Dispose(); $bmp.Dispose(); L "screen $name"
}
function All($win) { $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
function OfKind($win, [string]$kind) {
  @(All $win | Where-Object { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$kind" -or $_.Current.ClassName -eq "TNew$kind" })
}
function Btn($win, [string]$name) {
  foreach ($b in (OfKind $win 'Button')) {
    $n = ([string]$b.Current.Name).Replace('&', '').Trim()
    if ($n -eq $name -or $n -eq ($name -replace ' >$', '')) { return $b }
  }
  return $null
}
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
function State([string]$when) { L "state $when : app=$(Test-Path $Exe) data=$(Test-Path $Data) key=$(Test-Path $Key) ver=$(DisplayVer)" }
function DisplayVer { try { (Get-ItemProperty $Key -ea Stop).DisplayVersion } catch { '-' } }
function FileVer { try { (Get-Item $Exe).VersionInfo.ProductVersion } catch { '-' } }
function KillApp {
  Get-Process -ea SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins*' -or $_.ProcessName -eq 'IPbind Beta' } | Stop-Process -Force -ea SilentlyContinue
  Start-Sleep 1
}
function SilentInstall([string]$msi, [string]$tag) {
  L "silent install $tag : $msi"
  Start-Process $msi -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
  Start-Sleep 2
  State "after silent $tag"
}
function Scale([int]$pct) {
  $report = Join-Path $Out "setscale-$pct.txt"
  Start-Process $SetScale -ArgumentList @('setscale', "$pct", $report) -Wait
  $r = if (Test-Path $report) { Get-Content $report -Raw } else { 'no report' }
  L "setscale $pct -> $r"
  Start-Sleep 2
}
function ClearReminder { if (Test-Path $Data) { Remove-Item -Force -ea SilentlyContinue (Join-Path $Data 'dhcp-reminder-dismissed.txt') } }
function DismissReminder([string]$shotName) {
  Start-Sleep 2
  $rem = $null
  foreach ($w in $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -eq 'IPbind Beta' -and (Checks $w).Count -ge 2 -and (Btn $w 'Continue')) { $rem = $w }
  }
  if (-not $rem) { $rem = FindWin 'IPbind Beta' 8 }
  if ($rem -and (Btn $rem 'Continue')) {
    if ($shotName) { Shot $rem $shotName; L "popup texts: $(Texts $rem)" }
    $c = Checks $rem
    if ($c.Count -ge 1) { Click $c[0] | Out-Null; Start-Sleep -Milliseconds 400 }
    Click (Btn $rem 'Continue') | Out-Null
    Start-Sleep 1
    return $true
  }
  L 'WARN reminder not found'
  return $false
}
function LaunchAndGetMain([string]$popupShot) {
  if (-not (Test-Path $Exe)) { Fail "exe missing at $Exe"; return $null }
  Start-Process $Exe
  if ($popupShot) { DismissReminder $popupShot | Out-Null } else { DismissReminder $null | Out-Null }
  $main = FindWin 'IPbind Beta v*' 15 -Like
  if (-not $main) { Fail 'main window not found'; ShotScreen 'no-main'; return $null }
  L "main title: $($main.Current.Name)"
  return $main
}
function OpenAbout($main, [string]$shotName) {
  $aboutBtn = Btn $main 'About'
  if (-not $aboutBtn) { Fail 'About button missing'; return $null }
  Click $aboutBtn | Out-Null
  $ab = FindWin 'About IPbind Beta' 10
  if (-not $ab) { Fail 'About dialog missing'; return $null }
  Start-Sleep 1
  if ($shotName) { Shot $ab $shotName; L "about texts: $(Texts $ab)" }
  return $ab
}
function CloseAbout($ab) {
  if (-not $ab) { return }
  $cl = Btn $ab 'Close'
  if ($cl) { Click $cl | Out-Null } else { [W]::PostMessage([IntPtr]$ab.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
  Start-Sleep -Milliseconds 400
}
function OpenDropdowns($main, [string]$tag) {
  $boxes = @(All $main | Where-Object {
    $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' -or
    $_.Current.ClassName -like '*COMBOBOX*'
  })
  L "combos found: $($boxes.Count)"
  if ($boxes.Count -lt 1) { Fail "no combos at $tag"; return }
  # Adapter dropdown
  try { $boxes[0].SetFocus() } catch { L "focus adapter failed: $_" }
  [System.Windows.Forms.SendKeys]::SendWait('%{DOWN}')
  Start-Sleep -Milliseconds 700
  ShotScreen "$tag-adapter-dropdown-open"
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Milliseconds 300
  # Gateway support on so the second combo is visible
  $gw = Checks $main | Where-Object { $_.Current.Name -eq 'Gateway support' } | Select-Object -First 1
  if ($gw) { Click $gw | Out-Null; Start-Sleep -Milliseconds 800; Shot $main "$tag-main-gateway" }
  $boxes = @(All $main | Where-Object {
    $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' -or
    $_.Current.ClassName -like '*COMBOBOX*'
  })
  if ($boxes.Count -ge 2) {
    try { $boxes[1].SetFocus() } catch { L "focus gateway combo failed: $_" }
    [System.Windows.Forms.SendKeys]::SendWait('%{DOWN}')
    Start-Sleep -Milliseconds 700
    ShotScreen "$tag-gateway-dropdown-open"
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 300
  } else { L "WARN gateway combo not found (count=$($boxes.Count))" }
}

KillApp
if (Test-Path $Key) {
  $us = (Get-ItemProperty $Key).UninstallString
  if ($us -match '"([^"]+)"') {
    L 'uninstalling any leftover Beta first'
    Start-Process $Matches[1] -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/REMOVEDATA=1' -Wait
  }
}
KillApp
if (Test-Path $Data) { Remove-Item -Recurse -Force $Data }
if (Test-Path $AppDir) { Remove-Item -Recurse -Force $AppDir -ea SilentlyContinue }

# ---- 1. Install published 2.3.135, then 2.3.136 over it ----
Scale 100
SilentInstall $Installer135 '2.3.135'
$v135 = DisplayVer
L "Installed apps after 135: $v135"
if ($v135 -ne '2.3.135') { Fail "expected DisplayVersion 2.3.135, got $v135" }

L 'wizard install 2.3.136 over 2.3.135'
Start-Process $Installer136
$wiz = FindWin 'Setup - IPbind Beta*' 30 -Like
if (-not $wiz) { Fail '136 wizard not found'; ShotScreen 'no-wizard-136' }
else {
  for ($i = 1; $i -le 12; $i++) {
    Start-Sleep -Milliseconds 1500
    Shot $wiz ("install-over-136-{0:D2}" -f $i)
    L "page $i texts: $(Texts $wiz)"
    $accept = OfKind $wiz 'RadioButton' | Where-Object { $_.Current.Name -like 'I accept*' } | Select-Object -First 1
    if ($accept) { L 'accept licence'; Click $accept | Out-Null; Start-Sleep -Milliseconds 400 }
    $fin = Btn $wiz 'Finish'
    if ($fin) { L 'finish page'; Click $fin | Out-Null; break }
    $next = Btn $wiz 'Next >'; if (-not $next -or -not $next.Current.IsEnabled) { $i2 = Btn $wiz 'Install'; if ($i2) { $next = $i2 } }
    if ($next) { L "click $($next.Current.Name)"; Click $next | Out-Null } else { L "no Next/Install; buttons: $((OfKind $wiz 'Button' | ForEach-Object { $_.Current.Name }) -join ', ')" }
  }
}
Start-Sleep 3
# Finish may have launched the app; close it so InstalledVersionSync has run, then re-check.
$launched = FindWin 'IPbind Beta v*' 8 -Like
if (-not $launched) { DismissReminder 'first-launch-popup' | Out-Null; $launched = FindWin 'IPbind Beta v*' 8 -Like }
if ($launched) { L "launched after finish: $($launched.Current.Name)"; Shot $launched 'after-install-over-main' }
Start-Sleep 2
$v136 = DisplayVer
L "Installed apps after 136 over 135 (and launch): $v136  file=$($FileVer)"
if ($v136 -ne '2.3.136') { Fail "Installed apps did not show 2.3.136 (got $v136)" }
if ($launched -and $launched.Current.Name -notlike '*v2.3.136b*') { Fail "title bar was '$($launched.Current.Name)', expected 2.3.136b" }
KillApp

# ---- 2. About, popup, dropdowns at 100 / 150 / 175 ----
foreach ($pct in 100, 150, 175) {
  Scale $pct
  ClearReminder
  $popupName = "popup-$pct"
  $main = LaunchAndGetMain $popupName
  if ($main) {
    Shot $main "main-$pct"
    if ($main.Current.Name -notlike '*v2.3.136b*') { Fail "title at $pct% was '$($main.Current.Name)'" }
    $ab = OpenAbout $main "about-$pct"
    CloseAbout $ab
    OpenDropdowns $main "main-$pct"
    if ($pct -eq 150) { Shot $main 'final-main-150-gateway' }
  }
  KillApp
}

# ---- 3. About at 100% after a live move from high scale ----
Scale 175
ClearReminder
$main = LaunchAndGetMain 'popup-before-dpi-move'
if ($main) {
  $ab = OpenAbout $main 'about-175-before-move'
  if ($ab) {
    Scale 100
    Start-Sleep 2
    Shot $ab 'about-100-after-175-move'
    L "about after move texts: $(Texts $ab)"
    CloseAbout $ab
  }
}
KillApp

# ---- 4. In-app update: published 135 -> 136 ----
L 'in-app update path: uninstall 136, install 135, launch, click Update'
$unins = $null
if (Test-Path $Key) { $us = (Get-ItemProperty $Key).UninstallString; if ($us -match '"([^"]+)"') { $unins = $Matches[1] } }
if ($unins) { Start-Process $unins -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/REMOVEDATA=1' -Wait; Start-Sleep 2 }
KillApp
if (Test-Path $Data) { Remove-Item -Recurse -Force $Data }
SilentInstall $Installer135 '2.3.135-for-update'
if ((DisplayVer) -ne '2.3.135') { Fail "pre-update DisplayVersion was $(DisplayVer)" }
# Auto update checks on (installer default). Don't pre-create update-check.txt as manual.
ClearReminder
$main = LaunchAndGetMain 'popup-135-for-update'
if ($main) {
  Shot $main 'main-135-waiting-for-banner'
  $deadline = (Get-Date).AddSeconds(45)
  $updateBtn = $null
  while ((Get-Date) -lt $deadline -and -not $updateBtn) {
    Start-Sleep 2
    $updateBtn = Btn $main 'Update'
    $txt = Texts $main
    if ($txt -like '*available*') { L "banner texts: $txt" }
  }
  if (-not $updateBtn) { Fail 'update banner/Update button did not appear on 2.3.135'; Shot $main 'main-135-no-banner' }
  else {
    L 'clicking Update'
    Shot $main 'main-135-update-banner'
    Click $updateBtn | Out-Null
    $newMain = FindWin 'IPbind Beta v2.3.136b*' 60 -Like
    if (-not $newMain) {
      # update restarts; the reminder may appear first
      DismissReminder 'popup-after-update' | Out-Null
      $newMain = FindWin 'IPbind Beta v2.3.136b*' 20 -Like
    }
    if ($newMain) {
      L "after in-app update: $($newMain.Current.Name)  installed=$(DisplayVer) file=$($FileVer)"
      Shot $newMain 'main-after-in-app-update'
      if ((DisplayVer) -ne '2.3.136') { Fail "after in-app update Installed apps shows $(DisplayVer)" }
    } else { Fail 'app did not restart as 2.3.136b after in-app update'; ShotScreen 'no-main-after-update' }
  }
}
KillApp

# ---- 5. Uninstall with the remove-data step (interactive) ----
if (-not (Test-Path $Key)) { SilentInstall $Installer136 '2.3.136-for-uninstall' }
# Leave a data file so we can see it go away.
New-Item -ItemType Directory -Force $Data | Out-Null
Set-Content (Join-Path $Data 'walk-marker.txt') 'should-be-removed'
State 'before interactive remove-data uninstall'
$us = (Get-ItemProperty $Key).UninstallString
L "run UninstallString: $us"
Start-Process cmd.exe -ArgumentList '/c', "`"$us`"" -WindowStyle Hidden
$dlg = FindWin 'Uninstall IPbind Beta' 20
if (-not $dlg) { Fail 'uninstall prompt not found'; ShotScreen 'no-uninstall-prompt' }
else {
  Shot $dlg 'uninstall-remove-data-prompt'
  L "uninstall texts: $(Texts $dlg)"
  $c = Checks $dlg | Select-Object -First 1
  if ($c) { Click $c | Out-Null; Start-Sleep -Milliseconds 400; Shot $dlg 'uninstall-remove-data-checked' }
  else { Fail 'remove-data checkbox missing on uninstall prompt' }
  $u = Btn $dlg 'Uninstall'
  if ($u) { Click $u | Out-Null } else { Fail 'Uninstall button missing' }
  Start-Sleep 5
  ShotScreen 'uninstall-remove-data-after'
}
State 'after interactive remove-data uninstall'
if (Test-Path $Exe) { Fail 'app exe still present after remove-data uninstall' }
if (Test-Path $Data) { Fail 'app data still present after remove-data uninstall' }
if (Test-Path $Key) { Fail 'uninstall registry key still present after remove-data uninstall' }

L "WALK_FAILS=$fails"
if ($fails -gt 0) { exit 1 }
L 'done'
exit 0
