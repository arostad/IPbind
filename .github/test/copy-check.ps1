$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

# Fail on glued words in user-visible copy (e.g. "clickthe") and on a lone
# wrapped "server." string, which Andy caught in About / the DHCP popup.
$fails = 0
function Fail([string]$msg) { $script:fails++; Write-Host "FAIL: $msg" }

function StripLineComments([string]$text) {
  $nl = New-Object System.Text.StringBuilder
  foreach ($line in ($text -split "`r?`n", -1)) {
    $cut = $line
    $inStr = $false
    $esc = $false
    for ($i = 0; $i -lt $line.Length; $i++) {
      $ch = $line[$i]
      if ($inStr) {
        if ($esc) { $esc = $false }
        elseif ($ch -eq [char]'\') { $esc = $true }
        elseif ($ch -eq [char]'"') { $inStr = $false }
      }
      elseif ($ch -eq [char]'"') { $inStr = $true }
      elseif ($ch -eq [char]'/' -and ($i + 1) -lt $line.Length -and $line[$i+1] -eq [char]'/') {
        $cut = $line.Substring(0, $i)
        break
      }
    }
    [void]$nl.AppendLine($cut)
  }
  return $nl.ToString()
}

function StringLiterals([string]$text) {
  $text = StripLineComments $text
  $list = New-Object System.Collections.Generic.List[string]
  $m = [regex]::Matches($text, '"(?:\\.|[^"\\])*"')
  foreach ($hit in $m) { $list.Add($hit.Value) }
  return $list
}

$glued = @('clickthe','inthe','tothe','forthe','andthe','fromthe','withthe','onthe')
$files = @(
  @{ rel = 'IPbind.cs'; literals = $true },
  @{ rel = 'installer\IPbind.iss'; literals = $true },
  @{ rel = 'installer\privacy.txt'; literals = $false }
)
foreach ($f in $files) {
  $path = Join-Path $root $f.rel
  if (-not (Test-Path $path)) { continue }
  $text = Get-Content -Raw $path
  $chunks = if ($f.literals) { StringLiterals $text } else { @($text) }
  foreach ($chunk in $chunks) {
    foreach ($g in $glued) {
      $m = [regex]::Matches($chunk, $g, 'IgnoreCase')
      foreach ($hit in $m) {
        $i = $hit.Index
        $before = if ($i -gt 0) { $chunk[$i-1] } else { ' ' }
        $after = if ($i + $hit.Length -lt $chunk.Length) { $chunk[$i+$hit.Length] } else { ' ' }
        if ([char]::IsLetter($before) -or [char]::IsLetter($after)) { continue }
        Fail ("{0}: glued word '{1}' in copy: {2}" -f $f.rel, $hit.Value, $chunk.Trim())
      }
    }
  }
}

$cs = Get-Content -Raw (Join-Path $root 'IPbind.cs')
if ([regex]::IsMatch($cs, '"server\."')) {
  Fail 'IPbind.cs has a standalone "server." string; that wraps as a lone word.'
}
if ($cs -notmatch 'Look for this button in the main window') {
  Fail 'DHCP popup is missing the "Look for this button in the main window" caption.'
}

if ($fails -gt 0) {
  Write-Host "COPY_CHECK_FAILS=$fails"
  throw "Copy check failed ($fails)"
}
Write-Host 'COPY_CHECK_FAILS=0'
