# Builds the Lanternfall Browser add-on (Browser + YouTube screens) with the C# compiler that ships with Windows (C# 5).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $root '..\..\Sandbox\Playnite\Playnite.SDK.dll' }
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null
$refs = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.dll', 'System.Web.Extensions.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Xml.Linq.dll',
          'System.Net.Http.dll', 'Microsoft.CSharp.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
          (Join-Path $root 'lib\webview2\Microsoft.Web.WebView2.Core.dll'), (Join-Path $root 'lib\webview2\Microsoft.Web.WebView2.Wpf.dll'),
          "$fw\WPF\WindowsBase.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", $sdk) |
        ForEach-Object { "/r:$_" }
# every .cs file in this folder (not build/ or backups)
$sources = @(Get-ChildItem -LiteralPath $root -Filter *.cs -File | ForEach-Object { $_.FullName })
& "$fw\csc.exe" /nologo /target:library /optimize+ /nowarn:1701,1702 "/out:$out\SeleneBrowser.dll" $refs $sources
if ($LASTEXITCODE -ne 0) { throw 'SeleneBrowser build failed' }
Copy-Item (Join-Path $root 'extension.yaml'), (Join-Path $root 'icon.png') $out -Force
# Edge WebView2 SDK (Microsoft, signed) for the browser page: Playnite runs 32-bit, so the x86 loader sits beside the DLLs
Copy-Item (Join-Path $root 'lib\webview2\*.dll') $out -Force
# YouTube's web route (YouTubeWeb.cs): the hidden page's script and YouTube.js (MIT, lib\ytweb)
New-Item -ItemType Directory -Force (Join-Path $out 'ytweb') | Out-Null
Copy-Item (Join-Path $root 'ytweb\*'), (Join-Path $root 'lib\ytweb\youtubei.js'), (Join-Path $root 'lib\ytweb\LICENSE-youtubei.txt') (Join-Path $out 'ytweb') -Force
"built $out\SeleneBrowser.dll from $($sources.Count) files"
