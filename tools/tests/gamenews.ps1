$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path ([IO.Path]::GetTempPath()) ('Selene.GameNews.Check-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $references = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/r:$_" }
    & "$framework\csc.exe" /nologo /target:exe "/out:$testExe" $references (Join-Path $repo 'SeleneCore\src\SteamGameNews.cs') (Join-Path $PSScriptRoot 'GameNews.Check.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Game news checks did not compile' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Game news checks failed' }
} finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
