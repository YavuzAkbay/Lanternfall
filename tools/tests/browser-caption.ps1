# Regressions for YouTube caption spacing and the dark background behind cue text.
# Builds the Browser plugin and checks the real NormalizeCaptionVtt plus the player page's caption CSS.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

# Build Browser first so the DLL under test always matches the current source.
& (Join-Path $repo 'Browser\build.ps1') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Browser build failed' }

$output = Join-Path $PSScriptRoot 'obj\browser-caption'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item (Join-Path $repo 'Browser\build\*.dll') $output -Force

$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $repo '..\Sandbox\Playnite\Playnite.SDK.dll' }
if (Test-Path -LiteralPath $sdk) { Copy-Item $sdk $output -Force }

$refs = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll',
          (Join-Path $output 'SeleneBrowser.dll')) | ForEach-Object { "/r:$_" }
& "$framework\csc.exe" /nologo /target:exe "/out:$output\BrowserCaption.Check.exe" $refs (Join-Path $PSScriptRoot 'BrowserCaption.Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser caption check did not compile' }
& (Join-Path $output 'BrowserCaption.Check.exe')
if ($LASTEXITCODE -ne 0) { throw 'Browser caption checks failed' }
