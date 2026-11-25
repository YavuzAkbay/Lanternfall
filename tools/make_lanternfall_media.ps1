# Lanternfall's own pictures, drawn here (no third-party art): profile avatars, a soft wallpaper per theme palette,
# and the sample art the setup previews show. Windows PowerShell 5.1 + System.Drawing only.
#   tools\make_lanternfall_media.ps1            -> avatars + wallpapers + sample art
#   tools\make_lanternfall_media.ps1 -Only avatars|wallpapers|sample
# Wallpapers are only made for palettes that have none yet (gruvbox and kanagawa keep their public-domain paintings).
param([string]$Only = '')
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$theme = Join-Path $root 'theme'

function Hsv([double]$h, [double]$s, [double]$v, [int]$a = 255) {
    $h = (($h % 360) + 360) % 360; $c = $v * $s; $x = $c * (1 - [math]::Abs((($h / 60) % 2) - 1)); $m = $v - $c
    switch ([int][math]::Floor($h / 60)) { 0 { $r,$g,$b = $c,$x,0 } 1 { $r,$g,$b = $x,$c,0 } 2 { $r,$g,$b = 0,$c,$x } 3 { $r,$g,$b = 0,$x,$c } 4 { $r,$g,$b = $x,0,$c } default { $r,$g,$b = $c,0,$x } }
    [System.Drawing.Color]::FromArgb($a, [int](($r + $m) * 255), [int](($g + $m) * 255), [int](($b + $m) * 255))
}
function Hex([string]$s, [int]$a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($s); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }
function NewCanvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
    ,@($bmp, $g)
}
function Glow($g, [single]$cx, [single]$cy, [single]$rx, [single]$ry, $color) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $path.AddEllipse($cx - $rx, $cy - $ry, $rx * 2, $ry * 2)
    $br = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
    $br.CenterColor = $color; $br.SurroundColors = [System.Drawing.Color[]]@([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
    $g.FillPath($br, $path); $br.Dispose(); $path.Dispose()
}
function Save-Jpg($bmp, [string]$path, [int]$q = 88) {
    $enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $ep = New-Object System.Drawing.Imaging.EncoderParameters 1
    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]$q)
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $bmp.Save($path, $enc, $ep)
}
# a little lantern: cap, glass body with a lit core, base
function Lantern($g, [single]$cx, [single]$cy, [single]$s, $body, $light) {
    $cap = New-Object System.Drawing.SolidBrush $body
    $g.FillRectangle($cap, $cx - $s * 0.07, $cy - $s * 0.62, $s * 0.14, $s * 0.14)
    $g.FillPie($cap, $cx - $s * 0.34, $cy - $s * 0.58, $s * 0.68, $s * 0.5, 180, 180)
    $g.FillRectangle($cap, $cx - $s * 0.36, $cy + $s * 0.32, $s * 0.72, $s * 0.1)
    $glass = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, $light.R, $light.G, $light.B))
    $g.FillRectangle($glass, $cx - $s * 0.3, $cy - $s * 0.34, $s * 0.6, $s * 0.66)
    $pen = New-Object System.Drawing.Pen $body, ([single]($s * 0.04)); $g.DrawRectangle($pen, $cx - $s * 0.3, $cy - $s * 0.34, $s * 0.6, $s * 0.66)
    Glow $g $cx $cy ($s * 0.34) ($s * 0.34) $light
    $core = New-Object System.Drawing.SolidBrush $light; $g.FillEllipse($core, $cx - $s * 0.09, $cy - $s * 0.11, $s * 0.18, $s * 0.22)
    $cap.Dispose(); $glass.Dispose(); $pen.Dispose(); $core.Dispose()
}

if ($Only -eq '' -or $Only -eq 'avatars') {
    $dir = Join-Path $theme 'Avatars'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Get-ChildItem $dir -File | Remove-Item -Force
    for ($i = 0; $i -lt 36; $i++) {
        $hue = ($i % 12) * 30 + 8; $shape = [math]::Floor($i / 12)
        $c = NewCanvas 512 512; $bmp = $c[0]; $g = $c[1]
        $rect = New-Object System.Drawing.Rectangle 0, 0, 512, 512
        $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (Hsv $hue 0.55 0.32), (Hsv ($hue + 28) 0.7 0.12), 55
        $g.FillRectangle($lg, $rect); $lg.Dispose()
        Glow $g 256 300 300 300 (Hsv ($hue + 10) 0.5 0.55 120)
        $light = Hsv ($hue + 14) 0.25 1.0; $body = Hsv $hue 0.15 0.92
        switch ($shape) {
            0 { Lantern $g 256 262 300 $body $light }
            1 { $br = New-Object System.Drawing.SolidBrush $light; $g.FillEllipse($br, 126, 126, 260, 260)
                $cut = New-Object System.Drawing.SolidBrush (Hsv ($hue + 8) 0.6 0.2); $g.FillEllipse($cut, 190, 104, 250, 250); $br.Dispose(); $cut.Dispose() }
            default { $br = New-Object System.Drawing.SolidBrush $light
                $pts = @(); for ($k = 0; $k -lt 8; $k++) { $r = if ($k % 2 -eq 0) { 150 } else { 44 }; $a = [math]::PI / 4 * $k - [math]::PI / 2
                    $pts += New-Object System.Drawing.PointF ([single](256 + $r * [math]::Cos($a))), ([single](256 + $r * [math]::Sin($a))) }
                $g.FillPolygon($br, [System.Drawing.PointF[]]$pts); $br.Dispose() }
        }
        Save-Jpg $bmp (Join-Path $dir ('lantern-{0:00}.jpg' -f $i)) 88; $g.Dispose(); $bmp.Dispose()
    }
    Write-Host 'avatars: 36 drawn'
}

if ($Only -eq '' -or $Only -eq 'wallpapers') {
    $keep = @('gruvbox', 'kanagawa')   # public-domain paintings stay
    $wall = Join-Path $theme 'Wallpapers'
    Get-ChildItem $wall -Directory | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Recurse -Force
    Get-ChildItem (Join-Path $root 'tools\palettes') -Filter *.toml | ForEach-Object {
        $name = $_.BaseName; if ($keep -contains $name) { return }
        $t = @{}; Get-Content $_.FullName | ForEach-Object { if ($_ -match '^\s*(\w+)\s*=\s*"([^"]*)"') { $t[$Matches[1]] = $Matches[2] } }
        if (-not $t.background -or -not $t.accent) { return }
        $c = NewCanvas 1920 1080; $bmp = $c[0]; $g = $c[1]
        $g.Clear((Hex $t.background))
        $dark = ($t.mode -ne 'light')
        Glow $g 1400 380 1100 760 (Hex $t.accent $(if ($dark) { 90 } else { 110 }))
        Glow $g 380 900 900 600 (Hex $t.selection 150)
        $rnd = New-Object System.Random ($name.GetHashCode() -band 0xffff)
        for ($k = 0; $k -lt 26; $k++) {   # drifting lantern lights
            $x = $rnd.Next(60, 1860); $y = $rnd.Next(80, 1000); $r = $rnd.Next(6, 26)
            Glow $g $x $y ($r * 4) ($r * 4) (Hex $t.accent 40)
            $br = New-Object System.Drawing.SolidBrush (Hex $t.foreground 150); $g.FillEllipse($br, $x - $r / 2, $y - $r / 2, $r, $r); $br.Dispose()
        }
        Save-Jpg $bmp (Join-Path $wall "$name\1-lanternfall.jpg") 86; $g.Dispose(); $bmp.Dispose()
    }
    Write-Host 'wallpapers: drawn for palettes without a painting'
}

if ($Only -eq '' -or $Only -eq 'sample') {
    $dir = Join-Path $theme 'Media\Sample'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Get-ChildItem $dir -File | Remove-Item -Force
    $c = NewCanvas 1920 1080; $bmp = $c[0]; $g = $c[1]
    $rect = New-Object System.Drawing.Rectangle 0, 0, 1920, 1080
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (Hsv 215 0.55 0.25), (Hsv 265 0.6 0.09), 60; $g.FillRectangle($lg, $rect); $lg.Dispose()
    Glow $g 1250 420 900 600 (Hsv 40 0.6 0.9 110); Lantern $g 1250 470 520 (Hsv 40 0.2 0.9) (Hsv 42 0.4 1.0)
    Save-Jpg $bmp (Join-Path $dir 'expedition33-hero.jpg') 88; $g.Dispose(); $bmp.Dispose()
    $covers = [ordered]@{ 'expedition33-cover.jpg' = 215; 'cover-1145350.jpg' = 340; 'cover-1030300.jpg' = 180; 'cover-1086940.jpg' = 25; 'cover-1245620.jpg' = 285; 'cover-1091500.jpg' = 55 }
    foreach ($kv in $covers.GetEnumerator()) {
        $c = NewCanvas 600 900; $bmp = $c[0]; $g = $c[1]; $hue = [double]$kv.Value
        $rect = New-Object System.Drawing.Rectangle 0, 0, 600, 900
        $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (Hsv $hue 0.6 0.3), (Hsv ($hue + 30) 0.7 0.08), 80; $g.FillRectangle($lg, $rect); $lg.Dispose()
        Glow $g 300 360 360 360 (Hsv ($hue + 10) 0.5 0.9 110); Lantern $g 300 380 300 (Hsv $hue 0.15 0.92) (Hsv ($hue + 14) 0.3 1.0)
        Save-Jpg $bmp (Join-Path $dir $kv.Key) 86; $g.Dispose(); $bmp.Dispose()
    }
    $c = NewCanvas 700 260; $bmp = $c[0]; $g = $c[1]   # a transparent emblem in place of a title logo
    Glow $g 350 130 300 110 (Hsv 40 0.5 1.0 90); Lantern $g 350 130 200 (Hsv 40 0.15 0.95) (Hsv 42 0.35 1.0)
    $bmp.Save((Join-Path $dir 'expedition33-logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    Write-Host 'sample art drawn'
}
