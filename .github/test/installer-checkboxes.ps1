# Open the IPbind Beta installer and uninstaller, screenshot the Tasks page,
# Finish page, and uninstall prompt, and fail if any Inno checkbox is clipped.
param([Parameter(Mandatory=$true)][string]$Installer, [Parameter(Mandatory=$true)][string]$Out, [string]$Tag='ic', [switch]$ExpectClip, [switch]$AllowClip)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Wic {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@
[Wic]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out "installer-checkboxes-$Tag.txt"
function L([string]$s) { $line = "[$((Get-Date).ToString('HH:mm:ss.fff'))] $s"; Add-Content $log $line; Write-Host $line }
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
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
  $h = [IntPtr]$win.Current.NativeWindowHandle
  $r = New-Object Wic+RECT; [Wic]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
  $path = Join-Path $Out "$Tag-$name.png"
  $bmp.Save($path); $g.Dispose(); $bmp.Dispose()
  L "shot $name ($w x $hh) $path"
  $clipScript = Join-Path $PSScriptRoot 'check-inno-checkbox-clip.ps1'
  & powershell -ExecutionPolicy Bypass -File $clipScript -Path $path -Log $log
  $clipExit = $LASTEXITCODE
  $candLine = @(Select-String -Path $log -Pattern "INNO_CHECKBOX_CLIP=\d+ candidates=(\d+) file=$([regex]::Escape($path))" -ErrorAction SilentlyContinue | Select-Object -Last 1)
  $cands = -1
  if ($candLine) {
    if ($candLine.Line -match 'candidates=(\d+)') { $cands = [int]$Matches[1] }
  }
  if ($clipExit -gt 1) { L "FAIL $name clip-detector error (exit $clipExit)" }
  elseif ($AllowClip) {
    if ($clipExit -ge 1) { L "NOTE $name clipped at this scale (exit $clipExit candidates=$cands) -- recorded, not a job fail" }
    elseif (($name -eq 'tasks' -or $name -eq 'finish') -and $cands -eq 0) { L "FAIL $name clip check found no checkbox candidates" }
    else { L "NOTE $name unclipped at this scale (candidates=$cands)" }
  }
  elseif ($ExpectClip) {
    # The uninstall prompt is not the TNewCheckListBox that Andy's bug hit;
    # only Tasks and Finish are required to fail on the 2.3.139 installer.
    if ($name -eq 'uninstall-prompt') {
      L "OK $name detector exit $clipExit candidates=$cands (uninstall not required to clip)"
    }
    elseif ($clipExit -ge 1) { L "OK $name clipped as expected on 2.3.139 (exit $clipExit candidates=$cands)" }
    else { L "FAIL $name expected a clipped 2.3.139 checkbox, detector exit $clipExit candidates=$cands" }
  }
  else {
    if (($name -eq 'tasks' -or $name -eq 'finish') -and $cands -eq 0) { L "FAIL $name clip check found no checkbox candidates" }
    if ($clipExit -gt 0) { L "FAIL $name Inno checkbox left edge clipped ($clipExit)" }
  }
}
function All($win) { $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
$CT = [System.Windows.Automation.ControlType]
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
  [Wic]::PostMessage($h, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; return $true
}
function Texts($win) {
  ($win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
}

Get-Process -ea SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins*' -or $_.ProcessName -eq 'IPbind Beta' } | Stop-Process -Force -ea SilentlyContinue
L "installer: $Installer"
Start-Process $Installer
$wiz = FindWin 'Setup - IPbind Beta*' 30 -Like
if (-not $wiz) { L 'FAIL wizard window not found'; exit 1 }
$gotTasks = $false; $gotFinish = $false
for ($i = 1; $i -le 14; $i++) {
  Start-Sleep -Milliseconds 1500
  $t = Texts $wiz
  L "page $i texts: $t"
  # Match the Tasks page by its title. The Ready-to-Install summary also
  # contains "Check for updates at launch", which is how 73a4e91 shot the
  # wrong page (no checkbox) and the clip check passed with candidates=0.
  if ($t -match 'Select Additional Tasks') {
    Shot $wiz 'tasks'
    $gotTasks = $true
  } elseif ($t -match 'Launch IPbind') {
    Shot $wiz 'finish'
    $gotFinish = $true
    Click (Btn $wiz 'Cancel') | Out-Null
    # Finish page has Finish, not Cancel? Try Finish then kill, or close.
    break
  }
  $accept = OfKind $wiz 'RadioButton' | Where-Object { $_.Current.Name -like 'I accept*' } | Select-Object -First 1
  if ($accept) { Click $accept | Out-Null; Start-Sleep -Milliseconds 400 }
  $fin = Btn $wiz 'Finish'
  if ($fin -and -not $gotFinish) {
    Shot $wiz 'finish'
    $gotFinish = $true
    break
  }
  $next = Btn $wiz 'Next >'; if (-not $next -or -not $next.Current.IsEnabled) { $i2 = Btn $wiz 'Install'; if ($i2) { $next = $i2 } }
  if ($next) { Click $next | Out-Null }
}
if (-not $gotTasks) { L 'FAIL never saw the Tasks page' }
if (-not $gotFinish) { L 'FAIL never saw the Finish page' }

# Close the wizard without clicking Finish (that would launch the app).
# The files are already installed once we reached the Finish page.
Get-Process -ea SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'Setup - IPbind Beta*' } | Stop-Process -Force -ea SilentlyContinue
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'IPbind-Installer' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ea SilentlyContinue }
Start-Sleep 1
$unins = Join-Path $env:LOCALAPPDATA 'Programs\IPbind Beta\unins000.exe'
if (-not (Test-Path $unins)) {
  L 'unins000.exe missing after Finish page; silent-installing'
  Start-Process $Installer -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
}
if (-not (Test-Path $unins)) { L "FAIL unins000.exe missing" }
else {
  Start-Process $unins
  $dlg = FindWin 'Uninstall IPbind Beta' 20
  if (-not $dlg) { L 'FAIL uninstall prompt not found' }
  else {
    Start-Sleep 1
    Shot $dlg 'uninstall-prompt'
    # Cancel -- leave the install? Cleanup silent uninstall.
    $cancel = Btn $dlg 'Cancel'
    if ($cancel) { Click $cancel | Out-Null; Start-Sleep 2 }
  }
}
# Always try to clean the test install
if (Test-Path $unins) {
  Start-Process $unins -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
}
Get-Process 'IPbind Beta' -ea SilentlyContinue | Stop-Process -Force -ea SilentlyContinue
$fails = @(Select-String -Path $log -Pattern '\bFAIL\b' -ErrorAction SilentlyContinue).Count
if ($AllowClip) {
  $detErr = @(Select-String -Path $log -Pattern 'clip-detector error' -ErrorAction SilentlyContinue).Count
  $noCand = @(Select-String -Path $log -Pattern 'found no checkbox candidates' -ErrorAction SilentlyContinue).Count
  L "INSTALLER_CHECKBOX (allow-clip) detErr=$detErr noCand=$noCand tasks=$gotTasks finish=$gotFinish"
  if ($detErr -gt 0 -or $noCand -gt 0 -or -not $gotTasks -or -not $gotFinish) { exit 1 }
  exit 0
}
if ($ExpectClip) {
  $tasksClip = @(Select-String -Path $log -Pattern 'OK tasks clipped as expected' -ErrorAction SilentlyContinue).Count
  $finishClip = @(Select-String -Path $log -Pattern 'OK finish clipped as expected' -ErrorAction SilentlyContinue).Count
  L "INSTALLER_CHECKBOX_FAILS=$fails tasks=$gotTasks finish=$gotFinish tasksClip=$tasksClip finishClip=$finishClip (expect 139 clip)"
  if ($fails -gt 0 -or -not $gotTasks -or -not $gotFinish -or $tasksClip -lt 1 -or $finishClip -lt 1) { exit 1 }
  exit 0
}
L "INSTALLER_CHECKBOX_FAILS=$fails tasks=$gotTasks finish=$gotFinish"
if ($fails -gt 0 -or -not $gotTasks -or -not $gotFinish) { exit 1 }
exit 0
