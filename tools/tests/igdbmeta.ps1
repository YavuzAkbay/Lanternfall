$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path ([IO.Path]::GetTempPath()) ('Selene.IgdbMeta.Check-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $references = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/r:$_" }
    & "$framework\csc.exe" /nologo /target:exe "/out:$testExe" $references (Join-Path $repo 'SeleneCore\src\IgdbMetadataClient.cs') (Join-Path $PSScriptRoot 'IgdbMeta.Check.cs')
    if ($LASTEXITCODE -ne 0) { throw 'IGDB metadata checks did not compile' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'IGDB metadata checks failed' }
} finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
