$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path ([IO.Path]::GetTempPath()) ('Selene.BrowserTools.Check-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $references = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/r:$_" }
    & "$framework\csc.exe" /nologo /target:exe "/out:$testExe" $references (Join-Path $repo 'Browser\Downloads.cs') (Join-Path $repo 'Browser\ReaderScript.cs') (Join-Path $repo 'Browser\PageScroller.cs') (Join-Path $PSScriptRoot 'BrowserTools.Check.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Browser tools checks did not compile' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Browser tools checks failed' }
} finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
