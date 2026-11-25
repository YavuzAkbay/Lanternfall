# Regressions for browser tab/pad handling: LB/RB map to previous/next tab, and the
# redundant top-bar "Next tab" pill (and its stray 0xE8AD glyph) is gone.
# Builds the Browser plugin, checks source text, then exercises the real TabStep via reflection.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

# Build Browser first so the DLL under test always matches the current source.
& (Join-Path $repo 'Browser\build.ps1') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Browser build failed' }

# Source-level assertions: these cover changes that reflection alone cannot see.
$sourcePath = Join-Path $repo 'Browser\SeleneBrowser.cs'
$source = Get-Content -LiteralPath $sourcePath -Raw
if ($source.Contains('0xE8AD')) { throw 'Browser/SeleneBrowser.cs still contains the stray 0xE8AD glyph' }
if ($source.Contains('AddBarItem(bar, "Next tab"')) { throw 'Browser/SeleneBrowser.cs still contains the top-bar "Next tab" pill' }
$default = [regex]::Match($source, '(?m)^\s*else\s+h\s*=\s*"([^"]*)"')
if (-not $default.Success -or -not $default.Groups[1].Value.Contains('LB/RB Tabs')) {
    throw 'UpdateHints() default text does not contain "LB/RB Tabs"'
}

$output = Join-Path $PSScriptRoot 'obj\browser-tabs'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item (Join-Path $repo 'Browser\build\*.dll') $output -Force

$sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $repo '..\Sandbox\Playnite\Playnite.SDK.dll' }
if (Test-Path -LiteralPath $sdk) { Copy-Item $sdk $output -Force }

# The reflection check loads BrowserControl by name at runtime; its base type lives in the SDK assembly,
# so keep a copy next to the DLL under test (csc itself only needs SeleneBrowser.dll).
$sdkOut = Join-Path $output 'Playnite.SDK.dll'
$refs = @('System.dll', 'System.Core.dll',
          (Join-Path $output 'SeleneBrowser.dll'))
if (Test-Path -LiteralPath $sdkOut) { $refs += $sdkOut }
$refs = $refs | ForEach-Object { "/r:$_" }
& "$framework\csc.exe" /nologo /target:exe "/out:$output\BrowserTabs.Check.exe" $refs (Join-Path $PSScriptRoot 'BrowserTabs.Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser tabs check did not compile' }
& (Join-Path $output 'BrowserTabs.Check.exe')
if ($LASTEXITCODE -ne 0) { throw 'Browser tabs checks failed' }
