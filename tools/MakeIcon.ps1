# Draws "N1MM W2R NYQP" launcher icon: the real NY county map (from the app's embedded data) with the three labels.
# Usage: powershell -File tools\MakeIcon.ps1 [-Out "dist\N1MM W2R NYQP.ico"]   
param([string]$Out = (Join-Path $PSScriptRoot '..\dist\N1MM W2R NYQP.ico'))

Add-Type -AssemblyName System.Drawing
# Windows PowerShell cannot load the net8 Core dll, so read the county boundary file directly
# (format: see CountyLocator.ReadCounties).
$br = New-Object System.IO.BinaryReader ([System.IO.File]::OpenRead((Resolve-Path (Join-Path $PSScriptRoot '..\src\N1MMMobileCounty.Core\Data\ny_counties.bin'))))
[void]$br.ReadInt32()
$counties = New-Object 'System.Collections.Generic.List[object]'
$countyCount = $br.ReadInt32()
for ($ci = 0; $ci -lt $countyCount; $ci++) {
    [void]$br.ReadString(); [void]$br.ReadString()
    $rings = New-Object 'System.Collections.Generic.List[double[]]'
    $ringCount = $br.ReadInt32()
    for ($ri = 0; $ri -lt $ringCount; $ri++) {
        $n = $br.ReadInt32()
        $arr = New-Object 'double[]' ($n * 2)
        for ($i = 0; $i -lt $n * 2; $i++) { $arr[$i] = $br.ReadInt32() / 1e5 }
        $rings.Add($arr)
    }
    $counties.Add([pscustomobject]@{ Rings = $rings })
}$br.Close()

function New-Master([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square badge with a dusk-blue gradient.
    $r = [int]($S * 0.20)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect = New-Object System.Drawing.Rectangle 0, 0, ($S - 1), ($S - 1)
    $path.AddArc($rect.X, $rect.Y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(14, 36, 82)), ([System.Drawing.Color]::FromArgb(24, 96, 160)), 90.0
    $g.FillPath($grad, $path)
    $g.SetClip($path)

    # New York map: equirectangular with cos(lat) correction, fitted into the badge.
    $minLat = 40.45; $maxLat = 45.05; $minLon = -79.8; $maxLon = -71.8
    $k = [Math]::Cos(42.8 * [Math]::PI / 180)
    $mapW = ($maxLon - $minLon) * $k; $mapH = ($maxLat - $minLat)
    $scale = [Math]::Min($S * 0.94 / $mapW, $S * 0.94 / $mapH)
    $ox = ($S - $mapW * $scale) / 2; $oy = ($S - $mapH * $scale) / 2
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 150, 210, 250))
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 30, 70, 130)), ([Math]::Max(1.0, $S / 180.0))
    $edge.LineJoin = 'Round'
    foreach ($c in $counties) {
        foreach ($ring in $c.Rings) {
            $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
            for ($i = 0; $i -lt $ring.Length; $i += 2) {
                $x = $ox + ($ring[$i + 1] - $minLon) * $k * $scale
                $y = $oy + ($maxLat - $ring[$i]) * $scale
                $pts.Add((New-Object System.Drawing.PointF ([single]$x), ([single]$y)))
            }
            if ($pts.Count -lt 3) { continue }
            $arr = $pts.ToArray()
            $g.FillPolygon($fill, $arr)
            $g.DrawPolygon($edge, $arr)
        }
    }

    # Radio waves rising from the top-left corner.
    $wave = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 200, 40)), ($S * 0.028)
    $wave.StartCap = 'Round'; $wave.EndCap = 'Round'
    $cx = $S * 0.13; $cy = $S * 0.36
    foreach ($rad in @(0.07, 0.125, 0.18)) {
        $d = $S * $rad * 2
        $g.DrawArc($wave, [single]($cx - $d / 2), [single]($cy - $d / 2), [single]$d, [single]$d, 200, 140)
    }
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 200, 40))), [single]($cx - $S * 0.022), [single]($cy - $S * 0.022), [single]($S * 0.044), [single]($S * 0.044))
    $g.ResetClip()

    # Labels with a soft dark outline so they read over the map.
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    function Draw-Label([string]$text, [double]$cy, [double]$em, [System.Drawing.Color]$color) {
        $font = New-Object System.Drawing.Font 'Segoe UI Black', ([single]($S * $em)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gp.AddString($text, $font.FontFamily, [int]$font.Style, [single]($S * $em), (New-Object System.Drawing.PointF ([single]($S / 2)), ([single]($S * $cy))), $fmt)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 8, 20, 50)), ([single]($S * $em * 0.16))
        $pen.LineJoin = 'Round'
        $g.DrawPath($pen, $gp)
        $g.FillPath((New-Object System.Drawing.SolidBrush $color), $gp)
    }
    Draw-Label 'N1MM' 0.15 0.165 ([System.Drawing.Color]::White)
    Draw-Label 'W2R' 0.50 0.29 ([System.Drawing.Color]::FromArgb(255, 255, 200, 40))
    Draw-Label 'NYQP' 0.85 0.20 ([System.Drawing.Color]::White)
    $g.Dispose()
    return $bmp
}

$master = New-Master 512
$sizes = 256, 64, 48, 32, 24, 16
$pngs = foreach ($s in $sizes) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close(); $fs.Close()
$master.Save(([System.IO.Path]::ChangeExtension($Out, '.preview.png')), [System.Drawing.Imaging.ImageFormat]::Png)
"Wrote $Out"


