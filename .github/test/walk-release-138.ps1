# Real-user walkthrough of the PUBLISHED Beta 2.3.138 installer:
# install 2.3.137, install 138 over it, check Installed apps, launch, About and
# the DHCP popup at 100/150/175 and after a live scale change, open both
# dropdowns, 10s mouse-move flicker test at 100/150, in-app update from 137,
# uninstall with the remove-data step.
param(
  [Parameter(Mandatory=$true)][string]$Installer137,
  [Parameter(Mandatory=$true)][string]$Installer138,
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
$log = Join-Path $Out 'walk-release-138.txt'
$fails = 0
function L([string]$s) { $line = "[$((Get-Date).ToString('HH:mm:ss.fff'))] $s"; Add-Content $log $line; Write-Host $line }
function Fail([string]$s) { $script:fails++; L "FAIL $s" }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function WinNameMatch([string]$n, [string]$name, [switch]$Like) {
  if ($Like) { return $n -like $name }
  return $n -eq $name
}
function FindWin([string]$name, [int]$sec = 15, [switch]$Like) {
  $end = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $end) {
    $roots = @($AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))
    foreach ($w in $roots) {
      if (WinNameMatch $w.Current.Name $name -Like:$Like) { return $w }
    }
    # Owned dialogs (About) are sometimes nested under the owner in the UIA tree
    # instead of appearing as a root child. Search descendants too.
    foreach ($w in $roots) {
      try {
        foreach ($c in $w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
          if (WinNameMatch $c.Current.Name $name -Like:$Like) { return $c }
        }
      } catch {}
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
function Named($win, [string]$name) {
  foreach ($el in (All $win)) {
    $n = ([string]$el.Current.Name).Replace('&', '').Trim()
    if ($n -eq $name -or $n -eq ($name -replace ' >$', '') -or $n -like $name) { return $el }
  }
  return $null
}
function Btn($win, [string]$name) {
  $el = $null
  foreach ($b in (OfKind $win 'Button')) {
    $n = ([string]$b.Current.Name).Replace('&', '').Trim()
    if ($n -eq $name -or $n -eq ($name -replace ' >$', '')) { return $b }
  }
  return Named $win $name
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
function SuppressReminder {
  New-Item -ItemType Directory -Force $Data | Out-Null
  Set-Content (Join-Path $Data 'dhcp-reminder-dismissed.txt') 'walk'
}
function FindReminder([int]$sec = 15) {
  $end = (Get-Date).AddSeconds($sec)
  $logged = $false
  while ((Get-Date) -lt $end) {
    $roots = @($AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))
    if (-not $logged) {
      L ("root windows: " + (($roots | ForEach-Object { "'" + $_.Current.Name + "'" }) -join ", "))
      $logged = $true
    }
    foreach ($w in $roots) {
      $txt = Texts $w
      if ($txt -like '*Return to DHCP before*' -or $txt -like "*I'll click Return to DHCP*" -or $txt -like '*BEFORE YOU START*') {
        return $w
      }
    }
    Start-Sleep -Milliseconds 300
  }
  return $null
}
function DismissReminder([string]$shotName) {
  $rem = FindReminder 20
  if (-not $rem) { L 'WARN reminder not found'; return $false }
  if ($shotName) { Shot $rem $shotName; L "popup texts: $(Texts $rem)" }
  $ack = Named $rem "I'll click Return to DHCP before I plug into a live network"
  if (-not $ack) { $ack = Named $rem 'I understand*'; if (-not $ack) { $checks = Checks $rem; if ($checks.Count -ge 1) { $ack = $checks[0] } } }
  if ($ack) { Click $ack | Out-Null; Start-Sleep -Milliseconds 400 }
  $cont = Named $rem 'Continue'
  if ($cont) { Click $cont | Out-Null } else { Fail 'Continue not found on reminder'; return $false }
  $gone = (Get-Date).AddSeconds(8)
  while ((Get-Date) -lt $gone) {
    if (-not (FindReminder 1)) { Start-Sleep -Milliseconds 400; return $true }
    Start-Sleep -Milliseconds 200
  }
  L 'WARN reminder still open after Continue'
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
  $ab = FindWin 'About IPbind Beta*' 10 -Like
  if (-not $ab) {
    $names = @($AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { "'" + $_.Current.Name + "'" })
    L ("About missing; root windows: " + ($names -join ', '))
    Fail 'About dialog missing'
    ShotScreen ($shotName + '-no-about')
    return $null
  }
  Start-Sleep 1
  if ($shotName) { Shot $ab $shotName; L "about texts: $(Texts $ab)" }
  $txt = Texts $ab
  if ($txt -notlike '*Your IP address is never stored*') { Fail "About privacy note missing IP-not-stored line at $shotName" }
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
  return ,$boxes
}
function FlickerMove($main, $boxes, [string]$tag) {
  if (-not $main) { return }
  $h = [IntPtr]$main.Current.NativeWindowHandle
  $fr = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$fr) | Out-Null
  $targets = @()
  if ($boxes -and $boxes.Count -ge 1) { $targets += ,@('adapter', $boxes[0]) }
  if ($boxes -and $boxes.Count -ge 2) { $targets += ,@('gateway', $boxes[1]) }
  if ($targets.Count -eq 0) { L "WARN flicker: no combos at $tag"; return }
  foreach ($pair in $targets) {
    $name = $pair[0]; $box = $pair[1]
    try { $box.SetFocus() } catch { L "flicker focus $name failed: $_" }
    Start-Sleep -Milliseconds 200
    $end = (Get-Date).AddSeconds(10)
    $step = 0
    while ((Get-Date) -lt $end) {
      $phase = $step % 12
      $x = [int](($fr.L + $fr.R) / 2); $y = [int](($fr.T + $fr.B) / 2)
      try {
        $bh = [IntPtr]$box.Current.NativeWindowHandle
        $br = New-Object W+RECT; [W]::GetWindowRect($bh, [ref]$br) | Out-Null
        if ($phase -lt 4) {
          $x = $br.L + [int](($br.R - $br.L) * $phase / 3)
          $y = [int](($br.T + $br.B) / 2)
        } elseif ($phase -lt 8) {
          $pad = 16
          if ($phase -eq 4) { $x = $br.L - $pad; $y = $br.T - $pad }
          elseif ($phase -eq 5) { $x = $br.R + $pad; $y = $br.T - $pad }
          elseif ($phase -eq 6) { $x = $br.R + $pad; $y = $br.B + $pad }
          else { $x = $br.L - $pad; $y = $br.B + $pad }
        } else {
          if ($phase -eq 8) { $x = $fr.L + 8; $y = $fr.T + 8 }
          elseif ($phase -eq 9) { $x = $fr.R - 8; $y = $fr.T + 8 }
          elseif ($phase -eq 10) { $x = $fr.R - 8; $y = $fr.B - 8 }
          else { $x = [int](($fr.L + $fr.R) / 2); $y = [int](($fr.T + $fr.B) / 2) }
        }
      } catch {}
      try { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $x, $y } catch {}
      $step++
      Start-Sleep -Milliseconds 30
    }
    L "flicker $tag ${name}: 10s mouse move done"
    Shot $main "$tag-flicker-$name-after"
  }
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

# ---- 1. Install published 2.3.137, then 2.3.138 over it ----
Scale 100
SilentInstall $Installer137 '2.3.137'
$v137 = DisplayVer
L "Installed apps after 137: $v137"
if ($v137 -ne '2.3.137') { Fail "expected DisplayVersion 2.3.137, got $v137" }

L 'wizard install 2.3.138 over 2.3.137'
Start-Process $Installer138
$wiz = FindWin 'Setup - IPbind Beta*' 30 -Like
if (-not $wiz) { Fail '138 wizard not found'; ShotScreen 'no-wizard-138' }
else {
  for ($i = 1; $i -le 12; $i++) {
    Start-Sleep -Milliseconds 1500
    Shot $wiz ("install-over-138-{0:D2}" -f $i)
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
$v138 = DisplayVer
L "Installed apps after 138 over 137 (and launch): $v138  file=$($FileVer)"
if ($v138 -ne '2.3.138') { Fail "Installed apps did not show 2.3.138 (got $v138)" }
if ($launched -and $launched.Current.Name -notlike '*v2.3.138b*') { Fail "title bar was '$($launched.Current.Name)', expected 2.3.138b" }
KillApp

# ---- 2. Popup shots at 100 / 150 / 175 (reminder must appear) ----
foreach ($pct in 100, 150, 175) {
  Scale $pct
  KillApp
  ClearReminder
  if (-not (Test-Path $Exe)) { Fail "exe missing"; break }
  Start-Process $Exe
  if (DismissReminder "popup-$pct") { L "popup $pct dismissed after shot" } else { ShotScreen "popup-$pct-screen" }
  KillApp
}

# ---- 3. About, dropdowns, gateway at 100 / 150 / 175 (reminder suppressed) ----
foreach ($pct in 100, 150, 175) {
  Scale $pct
  SuppressReminder
  $main = LaunchAndGetMain $null
  if ($main) {
    Shot $main "main-$pct"
    if ($main.Current.Name -notlike '*v2.3.138b*') { Fail "title at $pct% was '$($main.Current.Name)'" }
    $ab = OpenAbout $main "about-$pct"
    CloseAbout $ab
    $boxes = OpenDropdowns $main "main-$pct"
    if ($pct -eq 100 -or $pct -eq 150) { FlickerMove $main $boxes "main-$pct" }
    if ($pct -eq 150) { Shot $main 'final-main-150-gateway' }
  }
  KillApp
}

# ---- 4. About at 100% after a live move from high scale ----
Scale 175
SuppressReminder
$main = LaunchAndGetMain $null
if ($main) {
  $ab = OpenAbout $main 'about-175-before-move'
  if ($ab) {
    Scale 100
    Start-Sleep 2
    Shot $ab 'about-100-after-175-move'
    L "about after move texts: $(Texts $ab)"
    CloseAbout $ab
  } else { Scale 100 }
} else { Scale 100 }
KillApp

# ---- 5. In-app update: published 136 -> 137 ----
Scale 100
L 'in-app update path: uninstall 138, install 137, launch, click Update'
$unins = $null
if (Test-Path $Key) { $us = (Get-ItemProperty $Key).UninstallString; if ($us -match '"([^"]+)"') { $unins = $Matches[1] } }
if ($unins) { Start-Process $unins -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/REMOVEDATA=1' -Wait; Start-Sleep 2 }
KillApp
if (Test-Path $Data) { Remove-Item -Recurse -Force $Data }
SilentInstall $Installer137 '2.3.137-for-update'
if ((DisplayVer) -ne '2.3.137') { Fail "pre-update DisplayVersion was $(DisplayVer)" }
# Auto update checks on (installer default). Don't pre-create update-check.txt as manual.
# Suppress the reminder so the UI thread can show and we can click the banner.
SuppressReminder
# Keep launch-check enabled (suppress only writes the reminder flag).
$main = LaunchAndGetMain $null
if ($main) {
  # Re-acquire main after the modal reminder has closed so the update
  # banner (which is posted once the UI thread is free) can appear.
  $main = FindWin 'IPbind Beta v*' 10 -Like
  Shot $main 'main-136-waiting-for-banner'
  $deadline = (Get-Date).AddSeconds(60)
  $updateBtn = $null
  while ((Get-Date) -lt $deadline -and -not $updateBtn) {
    Start-Sleep 2
    $main = FindWin 'IPbind Beta v*' 3 -Like
    if (-not $main) { continue }
    $updateBtn = Btn $main 'Update'
    $txt = Texts $main
    if ($txt -like '*available*') { L "banner texts: $txt" }
    else { L "update wait: no banner yet" }
  }
  if (-not $updateBtn) { Fail 'update banner/Update button did not appear on 2.3.137'; Shot $main 'main-136-no-banner' }
  else {
    L 'clicking Update'
    Shot $main 'main-136-update-banner'
    Click $updateBtn | Out-Null
    $newMain = FindWin 'IPbind Beta v2.3.138b*' 60 -Like
    if (-not $newMain) {
      # update restarts; the reminder may appear first
      DismissReminder 'popup-after-update' | Out-Null
      $newMain = FindWin 'IPbind Beta v2.3.138b*' 20 -Like
    }
    if ($newMain) {
      L "after in-app update: $($newMain.Current.Name)  installed=$(DisplayVer) file=$($FileVer)"
      Shot $newMain 'main-after-in-app-update'
      if ((DisplayVer) -ne '2.3.138') { Fail "after in-app update Installed apps shows $(DisplayVer)" }
    } else { Fail 'app did not restart as 2.3.138b after in-app update'; ShotScreen 'no-main-after-update' }
  }
}
KillApp

# ---- 6. Uninstall with the remove-data step (interactive) ----
if (-not (Test-Path $Key)) { SilentInstall $Installer138 '2.3.138-for-uninstall' }
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
