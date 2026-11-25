# Regressions for the codec strings the YouTube player hands to Edge WebView2 (finding H1).
# Compiles a tiny check against the real, freshly built Browser plugin - production source is not copied.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

# Build Browser first so the DLL under test always matches the current source.
& (Join-Path $repo 'Browser\build.ps1') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Browser build failed' }

$output = Join-Path $PSScriptRoot 'obj\browser-codec'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item (Join-Path $repo 'Browser\build\*.dll') $output -Force

$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $repo '..\Sandbox\Playnite\Playnite.SDK.dll' }
if (Test-Path -LiteralPath $sdk) { Copy-Item $sdk $output -Force }

$refs = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll',
          (Join-Path $output 'SeleneBrowser.dll')) | ForEach-Object { "/r:$_" }
& "$framework\csc.exe" /nologo /target:exe "/out:$output\BrowserCodec.Check.exe" $refs (Join-Path $PSScriptRoot 'BrowserCodec.Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser codec check did not compile' }
& (Join-Path $output 'BrowserCodec.Check.exe')
if ($LASTEXITCODE -ne 0) { throw 'Browser codec checks failed' }
