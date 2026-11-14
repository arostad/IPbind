# Regression: a 2.3.128 install that received a later in-app exe swap, then
# the new --apply-installer-update helper, must refresh unins000.exe/.dat and
# show the pre-uninstall prompt (remove-data checkbox), not the old Inno
# "completely remove IPbind" dialog.
param(
  [Parameter(Mandatory = $true)][string]$NewExe,
  [Parameter(Mandatory = $true)][string]$NewInstaller,
  [string]$Out = '',
  [switch]$SkipPrompt
)
$ErrorActionPreference = 'Stop'

$OldVersion = '2.3.128'
$OldUrl = "https://github.com/arostad/IPbind/releases/download/v$OldVersion/IPbind-Installer-v$OldVersion.exe"
$OldSha = '3009AB6CB4F571765D3DC24E53BDE218C5724151007C60169C9CAA8F8B02EF5B'
$Key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A42A329A-EDE5-4D63-B2C7-92947FE79E14}_is1'
$Work = Join-Path $env:TEMP ("ipbind-upgrade-uninstaller-" + [guid]::NewGuid().ToString('N'))
$InstDir = Join-Path $Work 'inst'
$OldSetup = Join-Path $Work "IPbind-Installer-v$OldVersion.exe"
$Pending = Join-Path $Work 'pending-setup.exe'
$log = if ($Out) { Join-Path $Out 'upgrade-uninstaller.txt' } else { $null }

function L([string]$s) {
  $line = "[$((Get-Date).ToString('HH:mm:ss.fff'))] $s"
  Write-Host $line
  if ($log) { Add-Content -Path $log -Value $line }
}
function Fail([string]$s) { L "FAIL: $s"; throw $s }

function Sha256([string]$path) {
  (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Stop-InstDirProcesses {
  # Elevated IPbind.exe often has an empty Path, so also match by name.
  # This test owns the machine for the duration of the run.
  Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match '^(IPbind|_unins|unins000)$' -or $_.ProcessName -like '_iu*' } |
    ForEach-Object {
      L "killing $($_.ProcessName) pid=$($_.Id)"
      Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
  cmd.exe /c 'taskkill /F /IM IPbind.exe /T >nul 2>&1' | Out-Null
  cmd.exe /c 'taskkill /F /IM unins000.exe /T >nul 2>&1' | Out-Null
}

function Uninstall-Silent {
  Stop-InstDirProcesses
  # Never use UninstallString (/SILENT /IPBINDPROMPT) for cleanup: that
  # shows the pre-uninstall prompt. /VERYSILENT on unins000.exe skips it.
  $unins = Join-Path $InstDir 'unins000.exe'
  if (Test-Path $unins) {
    L "silent uninstall via $unins"
    $u = Start-Process -FilePath $unins -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    L "silent uninstall exit $($u.ExitCode)"
  } elseif (Test-Path $Key) {
    L "WARN no unins000.exe; leaving $Key"
  }
  Stop-InstDirProcesses
}

New-Item -ItemType Directory -Force $Work | Out-Null
if ($Out) { New-Item -ItemType Directory -Force $Out | Out-Null }
$NewExe = (Resolve-Path $NewExe).Path
$NewInstaller = (Resolve-Path $NewInstaller).Path
$newInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($NewExe)
$newVersion = $newInfo.ProductVersion
L "new exe $NewExe version=$newVersion"
L "new installer $NewInstaller"
if ([string]::IsNullOrWhiteSpace($newVersion)) { Fail 'new exe has no ProductVersion' }

$scriptFailed = $false
$scriptError = $null
try {
  L "downloading $OldUrl"
  Invoke-WebRequest -Uri $OldUrl -OutFile $OldSetup -UseBasicParsing
  $got = Sha256 $OldSetup
  if ($got -ne $OldSha) { Fail "2.3.128 installer hash $got did not match $OldSha" }
  L "2.3.128 installer hash ok"

  if (Test-Path $Key) {
    Fail "prod IPbind is already registered at $Key; refusing to overwrite another install"
  }

  New-Item -ItemType Directory -Force $InstDir | Out-Null
  L "silent-install $OldVersion into $InstDir"
  $p128 = Start-Process -FilePath $OldSetup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$InstDir`"" -Wait -PassThru
  if ($p128.ExitCode -ne 0) { Fail "2.3.128 installer exit $($p128.ExitCode)" }
  $exe128 = Join-Path $InstDir 'IPbind.exe'
  $unins = Join-Path $InstDir 'unins000.exe'
  $uninsDat = Join-Path $InstDir 'unins000.dat'
  if (-not (Test-Path $exe128)) { Fail '2.3.128 IPbind.exe missing after install' }
  if (-not (Test-Path $unins)) { Fail 'unins000.exe missing after 2.3.128 install' }
  if (-not (Test-Path $Key)) { Fail "uninstall registry key missing after 2.3.128 install: $Key" }

  $reg = Get-ItemProperty $Key
  L "after 128 DisplayVersion=$($reg.DisplayVersion) UninstallString=$($reg.UninstallString)"
  if ($reg.DisplayVersion -ne $OldVersion) { Fail "expected DisplayVersion $OldVersion, got $($reg.DisplayVersion)" }
  if ($reg.UninstallString -match 'IPBINDPROMPT') { Fail '2.3.128 UninstallString already has /IPBINDPROMPT' }
  $uninsHashBefore = Sha256 $unins
  $datHashBefore = Sha256 $uninsDat
  $uninsTimeBefore = (Get-Item $unins).LastWriteTimeUtc

  # Simulate the old in-app updater: replace only IPbind.exe.
  Copy-Item -Path $NewExe -Destination $exe128 -Force
  $afterSwap = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe128).ProductVersion
  if ($afterSwap -ne $newVersion) { Fail "exe swap left ProductVersion $afterSwap, want $newVersion" }
  $uninsHashMid = Sha256 $unins
  if ($uninsHashMid -ne $uninsHashBefore) { Fail 'unins000.exe changed during exe-only swap; the 128 baseline is not stable' }
  L "simulated in-app exe swap to $newVersion; unins000 still $OldVersion ($uninsHashBefore)"

  Copy-Item -Path $NewInstaller -Destination $Pending -Force
  $pendingHash = Sha256 $Pending
  # Run as a uniquely named helper, the same way the app does, so Inno
  # CloseApplications does not wait on an IPbind.exe helper. Launch via
  # ProcessStartInfo so -Wait cannot stick on the restarted app window.
  $helperPath = Join-Path $Work '.IPbind-update-helper-test.exe'
  Copy-Item -Path $NewExe -Destination $helperPath -Force
  # 999999 is not a running process; the helper treats that as "parent already exited".
  $helperArgs = "--apply-installer-update `"$Pending`" `"$InstDir`" $pendingHash 999999"
  L "running helper: $helperPath $helperArgs"
  # UseShellExecute must be true: IPbind.exe requires elevation, and the
  # CI/test host may not already be elevated. WaitForExit on this PID only
  # (Start-Process -Wait can stick on the restarted app window).
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $helperPath
  $psi.Arguments = $helperArgs
  $psi.UseShellExecute = $true
  $helper = [System.Diagnostics.Process]::Start($psi)
  if (-not $helper) { Fail 'could not start the installer-update helper' }
  if (-not $helper.WaitForExit(120000)) {
    try { $helper.Kill() } catch { }
    Fail 'helper did not exit within 120s'
  }
  L "helper exit $($helper.ExitCode)"
  if ($helper.ExitCode -ne 0) { Fail "helper exit $($helper.ExitCode)" }
  $errFile = Join-Path $InstDir 'update-error.txt'
  if (Test-Path $errFile) { Fail "update-error.txt: $(Get-Content -Raw $errFile)" }

  # The helper restarts IPbind; don't leave it running.
  Start-Sleep -Seconds 2
  Stop-InstDirProcesses

  if (-not (Test-Path $unins)) { Fail 'unins000.exe missing after installer helper' }
  $uninsHashAfter = Sha256 $unins
  $datHashAfter = Sha256 $uninsDat
  $reg = Get-ItemProperty $Key
  L "after helper DisplayVersion=$($reg.DisplayVersion) UninstallString=$($reg.UninstallString)"
  L "unins000 hash before=$uninsHashBefore after=$uninsHashAfter"
  if ($uninsHashAfter -eq $uninsHashBefore) { Fail 'unins000.exe was not replaced by the installer helper' }
  if ($datHashAfter -eq $datHashBefore) { Fail 'unins000.dat was not replaced by the installer helper' }
  if ($reg.UninstallString -notmatch 'IPBINDPROMPT') { Fail 'UninstallString is missing /IPBINDPROMPT after helper' }
  if ($reg.DisplayVersion -ne $newVersion -and $reg.DisplayVersion -ne ($newVersion + 'b')) {
    Fail "DisplayVersion is $($reg.DisplayVersion), expected $newVersion"
  }
  $exeAfter = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $InstDir 'IPbind.exe')).ProductVersion
  if ($exeAfter -ne $newVersion) { Fail "installed exe is $exeAfter, expected $newVersion" }
  L "file and registry assertions passed"

  if (-not $SkipPrompt) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
    $AE = [System.Windows.Automation.AutomationElement]
    $TS = [System.Windows.Automation.TreeScope]
    function FindWin([string]$name, [int]$sec = 15) {
      $end = (Get-Date).AddSeconds($sec)
      while ((Get-Date) -lt $end) {
        foreach ($w in $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
          if ($w.Current.Name -eq $name) { return $w }
        }
        Start-Sleep -Milliseconds 250
      }
      return $null
    }
    function Texts($win) {
      $bits = New-Object System.Collections.Generic.List[string]
      foreach ($el in $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $n = $el.Current.Name
        if ($n) { [void]$bits.Add($n) }
      }
      return ($bits -join ' | ')
    }
    function ClosePrompt($win) {
      $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Cancel')
      $btn = $win.FindFirst($TS::Descendants, $cond)
      if ($btn) {
        try {
          $inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
          $inv.Invoke()
          return $true
        } catch { }
        try {
          $h = [IntPtr]$btn.Current.NativeWindowHandle
          if ($h -ne [IntPtr]::Zero) {
            Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class UuMsg {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}
"@ -ErrorAction SilentlyContinue
            [UuMsg]::PostMessage($h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            return $true
          }
        } catch { }
      }
      try {
        $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $wp.Close()
        return $true
      } catch { }
      return $false
    }

    $us = $reg.UninstallString
    L "launch UninstallString: $us"
    # Do not cmd /c this: /SILENT /IPBINDPROMPT shows a modal prompt, and a
    # synchronous launch would wait for Cancel before FindWin can run.
    $uninsExe = Join-Path $InstDir 'unins000.exe'
    $uninsProc = Start-Process -FilePath $uninsExe -ArgumentList '/SILENT /IPBINDPROMPT' -PassThru
    $dlg = FindWin "Uninstall IPbind" 25
    if (-not $dlg) {
      if ($Out) {
        $screen = Join-Path $Out 'upgrade-uninstaller-no-prompt.png'
        Add-Type -AssemblyName System.Windows.Forms
        $b = New-Object System.Drawing.Bitmap ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width), ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
        $b.Save($screen); $g.Dispose(); $b.Dispose()
        L "saved $screen"
      }
      try { if ($uninsProc -and -not $uninsProc.HasExited) { $uninsProc.Kill() } } catch { }
      Fail 'new uninstall prompt not found (title Uninstall IPbind)'
    }
    $text = Texts $dlg
    L "prompt texts: $text"
    if ($Out) {
      $shot = Join-Path $Out 'upgrade-uninstaller-prompt.png'
      $handle = [IntPtr]$dlg.Current.NativeWindowHandle
      Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class UuWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
"@
      $r = New-Object UuWin+RECT
      [UuWin]::GetWindowRect($handle, [ref]$r) | Out-Null
      $w = [Math]::Max(1, $r.R - $r.L); $h = [Math]::Max(1, $r.B - $r.T)
      $bmp = New-Object System.Drawing.Bitmap $w, $h
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
      $bmp.Save($shot); $g.Dispose(); $bmp.Dispose()
      L "saved $shot"
    }
    if ($text -notmatch 'Uninstall IPbind\?') {
      Fail "prompt is missing the Uninstall IPbind? title. texts: $text"
    }
    if ($text -notmatch 'Also remove all IPbind data') {
      Fail "prompt is missing the remove-data checkbox text. texts: $text"
    }
    if ($text -match 'completely remove IPbind') {
      Fail "old Inno uninstall dialog appeared after the helper. texts: $text"
    }
    # The gate is that the new prompt appeared with the right copy. Closing
    # it is cleanup: Inno's Cancel button may not expose InvokePattern, and
    # an elevated unins can ignore BM_CLICK from an unelevated test.
    if (-not (ClosePrompt $dlg)) { L "WARN could not send Cancel" }
    Start-Sleep -Seconds 2
    $still = FindWin "Uninstall IPbind" 1
    if ($still) {
      L "prompt still open; trying WindowPattern.Close"
      try {
        $still.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
      } catch { L "WindowPattern.Close failed: $_" }
      Start-Sleep -Seconds 2
    }
    if (FindWin "Uninstall IPbind" 1) {
      L "WARN prompt still open after close attempts; cleanup will stop it"
    }
    if (-not (Test-Path $exe128)) { Fail 'closing the prompt uninstalled the app' }
    L "new pre-uninstall prompt shown"
  }

  L "upgrade-uninstaller regression passed"
}
catch {
  $scriptFailed = $true
  $scriptError = $_
  L "ERROR: $($_.Exception.Message)"
}
finally {
  try { Uninstall-Silent } catch { L "cleanup uninstall error: $($_.Exception.Message)" }
  try { if (Test-Path $Work) { Remove-Item -Recurse -Force $Work } } catch { L "cleanup workdir error: $($_.Exception.Message)" }
}

if ($scriptFailed) { throw $scriptError }
exit 0
