# Pixel check: Inno/Win11 checkboxes must not have the left edge of the square
# clipped (hard vertical cut on the left, rounded on the right). Used by walk.ps1.
# Returns the number of clipped checkboxes found (0 = pass).
param(
  [Parameter(Mandatory = $true)][string]$Path,
  [string]$Log
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function IsBg([System.Drawing.Color]$c) {
  if ($c.R -gt 230 -and $c.G -gt 230 -and $c.B -gt 230) { return $true }
  # light-blue wizard graphic pane
  if ($c.B -gt 200 -and $c.G -gt 180 -and $c.R -gt 150 -and $c.B -gt $c.R -and [Math]::Abs([int]$c.G - [int]$c.B) -lt 40) { return $true }
  return $false
}
function IsBox([System.Drawing.Color]$c) {
  if ($c.B -ge 150 -and $c.R -le 80 -and $c.G -le 180 -and ($c.B - $c.R) -ge 70) { return $true }
  if ($c.R -ge 70 -and $c.R -le 180 -and $c.G -ge 70 -and $c.G -le 180 -and $c.B -ge 70 -and $c.B -le 180 `
      -and [Math]::Abs([int]$c.R - [int]$c.G) -lt 25 -and [Math]::Abs([int]$c.G - [int]$c.B) -lt 25 -and $c.R -lt 230) {
    return $true
  }
  return $false
}

$bmp = [System.Drawing.Bitmap]::FromFile($Path)
$clips = 0
$candidates = 0
$visited = New-Object 'System.Collections.Generic.HashSet[string]'
try {
  $w = $bmp.Width; $h = $bmp.Height
  for ($y = 1; $y -lt $h - 1; $y++) {
    for ($x = 1; $x -lt $w - 2; $x++) {
      if ($visited.Contains("$x,$y")) { continue }
      if (-not (IsBox $bmp.GetPixel($x, $y))) { continue }
      if (-not (IsBg $bmp.GetPixel($x - 1, $y))) { continue }
      $y2 = $y
      while ($y2 + 1 -lt $h) {
        if (-not (IsBox $bmp.GetPixel($x, $y2 + 1))) { break }
        if (-not (IsBg $bmp.GetPixel($x - 1, $y2 + 1))) { break }
        $y2++
      }
      $height = $y2 - $y + 1
      if ($height -lt 12 -or $height -gt 56) { continue }
      for ($yy = $y; $yy -le $y2; $yy++) { [void]$visited.Add("$x,$yy") }
      if ($x -le 2 -or $x -ge $w - 4) { continue }
      $mid = [int](($y + $y2) / 2)
      $x2 = $x
      while ($x2 + 1 -lt $w -and -not (IsBg $bmp.GetPixel($x2 + 1, $mid))) {
        if (($x2 - $x + 1) -gt ($height + 12)) { break }
        $x2++
      }
      $width = $x2 - $x + 1
      if ($width -lt 12 -or $width -gt ($height + 16)) { continue }
      $candidates++
      $band = [Math]::Max(2, [int]($height / 5))
      function EdgeX($yy, $fromLeft) {
        if ($fromLeft) {
          $xx0 = [Math]::Max(0, $x - 1); $xx1 = [Math]::Min($w - 1, $x + $width + 4)
          for ($xx = $xx0; $xx -le $xx1; $xx++) { if (IsBox $bmp.GetPixel($xx, $yy)) { return $xx } }
        } else {
          $xx0 = [Math]::Min($w - 1, $x + $width + 4); $xx1 = [Math]::Max(0, $x - 2)
          for ($xx = $xx0; $xx -ge $xx1; $xx--) { if (IsBox $bmp.GetPixel($xx, $yy)) { return $xx } }
        }
        return -1
      }
      $lm = -1; $rm = -1
      for ($yy = $mid - 1; $yy -le $mid + 1; $yy++) {
        if ($yy -lt 0 -or $yy -ge $h) { continue }
        $a = EdgeX $yy $true; if ($a -ge 0 -and ($lm -lt 0 -or $a -lt $lm)) { $lm = $a }
        $b = EdgeX $yy $false; if ($b -ge 0 -and $b -gt $rm) { $rm = $b }
      }
      if ($lm -lt 0 -or $rm -lt 0) { continue }
      $leftRound = 0; $rightRound = 0
      foreach ($range in @(@($y, $y + $band - 1), @($y2 - $band + 1, $y2))) {
        for ($yy = $range[0]; $yy -le $range[1]; $yy++) {
          if ($yy -lt 0 -or $yy -ge $h) { continue }
          $a = EdgeX $yy $true; if ($a -ge 0) { $leftRound = [Math]::Max($leftRound, $a - $lm) }
          $b = EdgeX $yy $false; if ($b -ge 0) { $rightRound = [Math]::Max($rightRound, $rm - $b) }
        }
      }
      $same = 0; $n = 0
      for ($yy = $y; $yy -le $y2; $yy++) {
        $a = EdgeX $yy $true
        if ($a -ge 0) { $n++; if ($a -eq $lm) { $same++ } }
      }
      $hardLeft = ($n -gt 0 -and $same -ge [int](0.85 * $n))
      if ($hardLeft -and $rightRound -ge 2 -and $leftRound -le 0) {
        $clips++
        $msg = "FAIL checkbox clipped in $Path at ($x,$y) ${width}x${height} leftRound=$leftRound rightRound=$rightRound"
        Write-Host $msg
        if ($Log) { Add-Content $Log $msg }
      }
    }
  }
}
finally { $bmp.Dispose() }

Write-Host "INNO_CHECKBOX_CLIP=$clips candidates=$candidates file=$Path"
if ($Log) { Add-Content $Log "INNO_CHECKBOX_CLIP=$clips candidates=$candidates file=$Path" }
exit $clips
