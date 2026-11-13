# Pixel check: Inno/Win11 checkboxes must not have the left edge of the square
# clipped (hard vertical cut on the left, rounded on the right). Used by walk.ps1
# and installer-checkboxes.ps1. Logs every candidate with coordinates and edge
# measurements. Returns the number of clipped checkboxes found (0 = pass).
param(
  [Parameter(Mandatory = $true)][string]$Path,
  [string]$Log
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Write-ClipLog([string]$msg) {
  Write-Host $msg
  if ($Log) { Add-Content -Path $Log -Value $msg }
}

function IsBg([System.Drawing.Color]$c) {
  if ($c.R -gt 220 -and $c.G -gt 220 -and $c.B -gt 220) { return $true }
  # light-blue wizard graphic pane
  if ($c.B -gt 200 -and $c.G -gt 180 -and $c.R -gt 150 -and $c.B -gt $c.R -and [Math]::Abs([int]$c.G - [int]$c.B) -lt 40) { return $true }
  return $false
}
function IsBox([System.Drawing.Color]$c) {
  # Checked accent blue (and AA close to it).
  if ($c.B -ge 150 -and $c.R -le 80 -and $c.G -le 180 -and ($c.B - $c.R) -ge 70) { return $true }
  # Unchecked mid-gray border.
  if ($c.R -ge 70 -and $c.R -le 180 -and $c.G -ge 70 -and $c.G -le 180 -and $c.B -ge 70 -and $c.B -le 180 `
      -and [Math]::Abs([int]$c.R - [int]$c.G) -lt 25 -and [Math]::Abs([int]$c.G - [int]$c.B) -lt 25 -and $c.R -lt 230) {
    return $true
  }
  return $false
}
# Include the light-blue / light-gray antialias of a rounded corner. Without
# this, a complete box's first visual row is missed and the core looks hard-left.
function IsBoxAa([System.Drawing.Color]$c) {
  if (IsBox $c) { return $true }
  if (IsBg $c) { return $false }
  if ($c.B -ge 160 -and ($c.B - $c.R) -ge 40 -and $c.G -ge 80 -and $c.R -le 160) { return $true }
  if ($c.R -ge 160 -and $c.R -le 230 -and $c.G -ge 160 -and $c.G -le 230 -and $c.B -ge 160 -and $c.B -le 230 `
      -and [Math]::Abs([int]$c.R - [int]$c.G) -lt 20 -and [Math]::Abs([int]$c.G - [int]$c.B) -lt 20) {
    return $true
  }
  return $false
}

function EdgeX([System.Drawing.Bitmap]$bmp, [int]$x, [int]$width, [int]$yy, [bool]$fromLeft, [bool]$useAa) {
  $w = $bmp.Width; $h = $bmp.Height
  if ($yy -lt 0 -or $yy -ge $h) { return -1 }
  if ($fromLeft) {
    $xx0 = [Math]::Max(0, $x - 3); $xx1 = [Math]::Min($w - 1, $x + $width + 8)
    for ($xx = $xx0; $xx -le $xx1; $xx++) {
      $c = $bmp.GetPixel($xx, $yy)
      if ($useAa) { if (IsBoxAa $c) { return $xx } } else { if (IsBox $c) { return $xx } }
    }
  } else {
    $xx0 = [Math]::Min($w - 1, $x + $width + 8); $xx1 = [Math]::Max(0, $x - 3)
    for ($xx = $xx0; $xx -ge $xx1; $xx--) {
      $c = $bmp.GetPixel($xx, $yy)
      if ($useAa) { if (IsBoxAa $c) { return $xx } } else { if (IsBox $c) { return $xx } }
    }
  }
  return -1
}

$clips = 0
$candidates = 0
$visited = New-Object 'System.Collections.Generic.HashSet[string]'
$bmp = $null
try {
  if (-not (Test-Path -LiteralPath $Path)) { throw "image not found: $Path" }
  $bmp = [System.Drawing.Bitmap]::FromFile($Path)
  $w = $bmp.Width; $h = $bmp.Height
  Write-ClipLog "CLIPSCAN $Path ${w}x${h}"
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
      $coreH = $y2 - $y + 1
      if ($coreH -lt 10 -or $coreH -gt 64) { continue }
      for ($yy = $y; $yy -le $y2; $yy++) { [void]$visited.Add("$x,$yy") }
      if ($x -le 2 -or $x -ge $w - 4) { continue }

      # Width and label gap on the upper quarter, not mid — mid often cuts
      # through the check glyph and the hole looks like a 1px "gap".
      $uy = $y + [Math]::Max(2, [int]($coreH / 5))
      if ($uy -gt $y2) { $uy = [int](($y + $y2) / 2) }
      $x2 = $x
      while ($x2 + 1 -lt $w -and (IsBox $bmp.GetPixel($x2 + 1, $uy))) {
        if (($x2 - $x + 1) -gt ($coreH + 10)) { break }
        $x2++
      }
      $width = $x2 - $x + 1
      if ($width -lt 10 -or $width -gt ($coreH + 10)) { continue }
      $gap = 0
      for ($xx = $x2 + 1; $xx -lt [Math]::Min($w, $x2 + 9); $xx++) {
        if (IsBg $bmp.GetPixel($xx, $uy)) { $gap++ } else { break }
      }
      if ($gap -lt 2) { continue }

      # Visual top/bottom including corner AA (the rows the core walk misses
      # because left-of-corner is not background).
      $top = $y
      while ($top - 1 -ge 1) {
        if ((EdgeX $bmp $x $width ($top - 1) $true $true) -lt 0) { break }
        $top--
      }
      $bot = $y2
      while ($bot + 1 -le $h - 2) {
        if ((EdgeX $bmp $x $width ($bot + 1) $true $true) -lt 0) { break }
        $bot++
      }
      $visH = $bot - $top + 1
      # Reject tall wizard-graphic runs that are not a square checkbox.
      if ($visH -gt ($width * 3) -or $visH -gt ($coreH + 24)) { continue }

      $a = EdgeX $bmp $x $width $uy $true $false
      $b = EdgeX $bmp $x $width $uy $false $false
      $baa = EdgeX $bmp $x $width $uy $false $true
      if ($a -lt 0) { continue }
      $lm = $a
      $rm = $b
      if ($baa -gt $rm) { $rm = $baa }
      if ($rm -lt 0) { continue }

      $same = 0; $n = 0
      for ($yy = $y; $yy -le $y2; $yy++) {
        $le = EdgeX $bmp $x $width $yy $true $false
        if ($le -ge 0) { $n++; if ($le -eq $lm) { $same++ } }
      }
      $hardLeft = ($n -gt 0 -and $same -ge [int](0.85 * $n))

      $la0 = EdgeX $bmp $x $width $top $true $true
      $ra0 = EdgeX $bmp $x $width $top $false $true
      $firstL = -1; $firstR = -1
      if ($la0 -ge 0) { $firstL = $la0 - $lm }
      if ($ra0 -ge 0) { $firstR = $rm - $ra0 }

      $t1 = [Math]::Min($bot, $top + 2)
      $topL = 0; $topR = 0
      for ($yy = $top; $yy -le $t1; $yy++) {
        $la = EdgeX $bmp $x $width $yy $true $true
        $ra = EdgeX $bmp $x $width $yy $false $true
        if ($la -ge 0) { $topL = [Math]::Max($topL, $la - $lm) }
        if ($ra -ge 0) { $topR = [Math]::Max($topR, $rm - $ra) }
      }

      $candidates++
      # Clipped Inno box: hard mid-left, the first visual row is already at
      # the mid left (no rounded top-left), and the same row is inset on the
      # right (the rounded corner that was not cut). A complete box has
      # firstL >= 1 (rounded top-left still present).
      $clipped = $hardLeft -and ($firstL -eq 0) -and ($firstR -ge 1)
      $verdict = $(if ($clipped) { 'CLIP' } else { 'ok' })
      $msg = ("CAND {0} at ({1},{2}) {3}x{4} coreH={5} vis=({6}..{7} h={8}) lm={9} rm={10} hardLeft={11} gap={12} firstL={13} firstR={14} topL={15} topR={16} uy={17}" -f `
        $verdict, $x, $y, $width, $coreH, $coreH, $top, $bot, $visH, $lm, $rm, $hardLeft, $gap, $firstL, $firstR, $topL, $topR, $uy)
      Write-ClipLog $msg
      if ($clipped) {
        $clips++
        Write-ClipLog ("FAIL checkbox clipped in {0} at ({1},{2}) {3}x{4} firstL={5} firstR={6} vis=({7}..{8})" -f $Path, $x, $y, $width, $coreH, $firstL, $firstR, $top, $bot)
      }
    }
  }
} catch {
  Write-ClipLog "FAIL clip-detector error on $Path : $($_.Exception.Message)"
  if ($bmp) { $bmp.Dispose() }
  exit 2
}
if ($bmp) { $bmp.Dispose() }
Write-ClipLog "INNO_CHECKBOX_CLIP=$clips candidates=$candidates file=$Path"
exit $clips
