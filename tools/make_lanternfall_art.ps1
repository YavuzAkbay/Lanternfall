# Exports Lanternfall's artwork (the lantern mark) with WPF, nothing to install:
#   theme\Images\applogo*.png/.ico, custom_cover_background.png, SeleneCore\icon.png, Browser\icon.png,
#   docs\logo\lanternfall-logo-dark/light.png, lanternfall-banner.png, tools\art\lanternfall-mark.svg
# The mark's geometry is the same as SeleneCore\src\ForkBrand.cs (72 x 72 units): change both together.
#   powershell -File tools\make_lanternfall_art.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$root = Split-Path $PSScriptRoot -Parent

# ---- the mark (keep in step with ForkBrand.cs)
$Ring  = 'M 36,3.6 A 4.4,4.4 0 1 1 35.99,3.6 Z'
$Cap   = 'M 28,12 L 44,12 L 51,20.2 L 21,20.2 Z'
$Frame = 'M 22.5,19.5 L 49.5,19.5 L 49.5,54 L 22.5,54 Z M 26,23 L 26,50.5 L 46,50.5 L 46,23 Z'
$Base  = 'M 19,53.4 L 53,53.4 L 49,61 L 23,61 Z'
$Flame = 'M 36,27 C 40.5,32 43,35.8 43,40.2 A 7,7 0 0 1 29,40.2 C 29,35.8 31.5,32 36,27 Z'

$Cream = '#FFF6E6'; $Amber = '#FFB547'; $Gold = '#FFE2A0'; $Ember = '#FF8A3D'
$NightTop = '#1B1838'; $NightBottom = '#07060F'; $Paper = '#FBF7F0'; $InkDark = '#1B1838'

$ns = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'

function Get-MarkXaml([double]$x, [double]$y, [double]$size, [string]$ink, [double]$glow = 0.55, [string]$glowColor = $Amber, [bool]$goldFlame = $true) {
    $s = $size / 72
    $flameFill = if ($goldFlame) { "<Path.Fill><LinearGradientBrush StartPoint='0.5,0' EndPoint='0.5,1'><GradientStop Color='$Gold' Offset='0'/><GradientStop Color='$Amber' Offset='0.55'/><GradientStop Color='$Ember' Offset='1'/></LinearGradientBrush></Path.Fill>" } else { "<Path.Fill><SolidColorBrush Color='$ink'/></Path.Fill>" }
    @"
<Canvas Canvas.Left='$x' Canvas.Top='$y' Width='72' Height='72'>
  <Canvas.RenderTransform><ScaleTransform ScaleX='$s' ScaleY='$s'/></Canvas.RenderTransform>
  <Ellipse Canvas.Left='6' Canvas.Top='8' Width='60' Height='60' Opacity='$glow'>
    <Ellipse.Fill><RadialGradientBrush>
      <GradientStop Color='$glowColor' Offset='0'/><GradientStop Color='#99$($glowColor.Substring(1))' Offset='0.3'/>
      <GradientStop Color='#30$($glowColor.Substring(1))' Offset='0.65'/><GradientStop Color='#00$($glowColor.Substring(1))' Offset='1'/>
    </RadialGradientBrush></Ellipse.Fill>
  </Ellipse>
  <Path Data='$Ring' Stroke='$ink' StrokeThickness='3.6'/>
  <Path Data='$Cap' Fill='$ink'/>
  <Path Data='F0 $Frame' Fill='$ink'/>
  <Path Data='$Base' Fill='$ink'/>
  <Path Data='$Flame'>$flameFill</Path>
</Canvas>
"@
}

# the name in Lato, widely spaced (one TextBlock per letter: WPF has no letter spacing)
$lato = 'file:///' + ((Join-Path $root 'theme\Fonts').Replace('\', '/')) + '/#Lato'
function Get-WordXaml([double]$cx, [double]$y, [double]$size, [string]$ink) {
    $letters = ('LANTERNFALL'.ToCharArray() | ForEach-Object { "<TextBlock Text='$_' FontFamily='$lato' FontSize='$size' Foreground='$ink' Margin='$($size * 0.16),0'/>" }) -join ''
    "<StackPanel Orientation='Horizontal' Canvas.Top='$y' x:Name='word' Tag='$cx'>$letters</StackPanel>"
}

function Save-Png([string]$xaml, [int]$w, [int]$h, [string]$out, [int]$outW = 0) {
    $canvas = [Windows.Markup.XamlReader]::Parse("<Canvas $ns Width='$w' Height='$h' ClipToBounds='True'>$xaml</Canvas>")
    $canvas.Measure((New-Object Windows.Size $w, $h)); $canvas.Arrange((New-Object Windows.Rect 0, 0, $w, $h)); $canvas.UpdateLayout()
    $word = $canvas.FindName('word')
    if ($word) { [Windows.Controls.Canvas]::SetLeft($word, [double]$word.Tag - $word.ActualWidth / 2); $canvas.UpdateLayout() }
    $scale = if ($outW -gt 0) { $outW / $w } else { 1 }
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]($w * $scale)), ([int]($h * $scale)), (96 * $scale), (96 * $scale), ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($canvas)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    $fs = [IO.File]::Create($out); try { $enc.Save($fs) } finally { $fs.Close() }
    "wrote " + $out.Substring($root.Length + 1)
}

$sky = "<Rectangle Width='{0}' Height='{1}'><Rectangle.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='$NightTop' Offset='0'/><GradientStop Color='$NightBottom' Offset='1'/></LinearGradientBrush></Rectangle.Fill></Rectangle>"
function Get-Disc([int]$size) {
    "<Ellipse Width='$size' Height='$size'><Ellipse.Fill><LinearGradientBrush StartPoint='0.25,0' EndPoint='0.75,1'><GradientStop Color='$NightTop' Offset='0'/><GradientStop Color='$NightBottom' Offset='1'/></LinearGradientBrush></Ellipse.Fill></Ellipse>"
}

$img = Join-Path $root 'theme\Images'
$logo = Join-Path $root 'docs\logo'

# app icons: the mark alone (a game with no icon), and on a night disc (add-on and app icons)
Save-Png (Get-MarkXaml 10 10 236 $Cream) 256 256 (Join-Path $img 'applogo_clean.png')
$disc = (Get-Disc 256) + (Get-MarkXaml 41 37 174 $Cream 0.6)
Save-Png $disc 256 256 (Join-Path $img 'applogo.png')
Save-Png $disc 256 256 (Join-Path $img 'applogo_full.png') 512
Save-Png $disc 256 256 (Join-Path $root 'SeleneCore\icon.png')
Save-Png $disc 256 256 (Join-Path $root 'Browser\icon.png')

# default cover: a dim lantern on the night sky
Save-Png (($sky -f 600, 900) + (Get-MarkXaml 170 315 260 $Cream 0.5)) 600 900 (Join-Path $img 'custom_cover_background.png')

# logo lockups and the social banner
Save-Png ("<Rectangle Width='1200' Height='800' Fill='$NightBottom'/>" + (Get-MarkXaml 420 70 360 $Cream 0.6) + (Get-WordXaml 600 520 88 $Cream)) 1200 800 (Join-Path $logo 'lanternfall-logo-dark.png')
Save-Png ("<Rectangle Width='1200' Height='800' Fill='$Paper'/>" + (Get-MarkXaml 420 70 360 $InkDark 0.45) + (Get-WordXaml 600 520 88 $InkDark)) 1200 800 (Join-Path $logo 'lanternfall-logo-light.png')
$rng = New-Object Random 11
$stars = (1..90 | ForEach-Object { "<Ellipse Canvas.Left='$($rng.Next(0,1280))' Canvas.Top='$($rng.Next(0,640))' Width='{0}' Height='{0}' Fill='$Cream' Opacity='$([Math]::Round(0.12 + $rng.NextDouble() * 0.45, 2))'/>" -f (1.2, 1.6, 2.2)[$rng.Next(0,3)] }) -join ''
$warm = "<Ellipse Canvas.Left='240' Canvas.Top='-120' Width='800' Height='640' Opacity='0.35'><Ellipse.Fill><RadialGradientBrush><GradientStop Color='#FF8A3D' Offset='0'/><GradientStop Color='#00FF8A3D' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>"
Save-Png (($sky -f 1280, 640) + $warm + $stars + (Get-MarkXaml 505 70 270 $Cream 0.65) + (Get-WordXaml 640 420 68 $Cream)) 1280 640 (Join-Path $logo 'lanternfall-banner.png')

# Windows icon: PNG frames in an .ico container
$sizes = 16, 24, 32, 48, 64, 256
$frames = foreach ($sz in $sizes) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "lanternfall-ico-$sz.png"
    Save-Png $disc 256 256 $tmp $sz | Out-Null
    , [IO.File]::ReadAllBytes($tmp); Remove-Item $tmp
}
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
[IO.File]::WriteAllBytes((Join-Path $img 'applogo.ico'), $ms.ToArray())
"wrote theme\Images\applogo.ico"

# the master drawing as SVG (currentColor ink, so it can be recolored)
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 72 72">
  <!-- Lanternfall's lantern: ring, cap, glass frame, base and flame. Drawn in currentColor. Same geometry as SeleneCore/src/ForkBrand.cs. -->
  <path d="$Ring" fill="none" stroke="currentColor" stroke-width="3.6"/>
  <path d="$Cap" fill="currentColor"/>
  <path d="$Frame" fill="currentColor" fill-rule="evenodd"/>
  <path d="$Base" fill="currentColor"/>
  <path id="flame" d="$Flame" fill="currentColor"/>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $root 'tools\art\lanternfall-mark.svg'), $svg.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
"wrote tools\art\lanternfall-mark.svg"
