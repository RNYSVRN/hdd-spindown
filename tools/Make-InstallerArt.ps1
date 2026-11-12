param([string]$Root = (Split-Path $PSScriptRoot -Parent))
# Generates the WixUI bitmaps (banner 493x58, dialog 493x312) from the app icon
# in a plain Windows 11 look. Output is committed under installer\.
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile("$Root\assets\HDD-Spindown.png")
function New-Art([int]$w, [int]$h, [string]$out, [scriptblock]$draw) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'ClearTypeGridFit'
  & $draw $g
  $g.Dispose(); $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
}
# Banner: white, icon on the right (WixUI draws the title text on the left).
New-Art 493 58 "$Root\installer\banner.bmp" {
  param($g)
  $g.Clear([System.Drawing.Color]::White)
  $g.DrawImage($icon, 493 - 52, 9, 40, 40)
}
# Dialog: left panel 164 px with a soft gradient, icon and name; rest white for WixUI text.
New-Art 493 312 "$Root\installer\dialog.bmp" {
  param($g)
  $g.Clear([System.Drawing.Color]::White)
  $rect = New-Object System.Drawing.Rectangle 0, 0, 164, 312
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(232, 240, 250)), ([System.Drawing.Color]::FromArgb(212, 226, 244)), 90
  $g.FillRectangle($brush, $rect)
  $g.DrawImage($icon, 42, 70, 80, 80)
  $font = New-Object System.Drawing.Font 'Segoe UI Semibold', 13
  $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
  $g.DrawString('HDD Spindown', $font, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF 0, 165, 164, 30), $fmt)
}
$icon.Dispose()
"Installer-Grafiken erzeugt."
