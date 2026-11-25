$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path ([IO.Path]::GetTempPath()) ('Selene.PhoneRemote.Check-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $references = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/r:$_" }
    & "$framework\csc.exe" /nologo /target:exe "/out:$testExe" $references (Join-Path $repo 'Browser\PhoneNet.cs') (Join-Path $repo 'Browser\PhoneRemote.cs') (Join-Path $repo 'Browser\PhoneRemotePage.cs') (Join-Path $PSScriptRoot 'PhoneRemote.Check.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Phone remote checks did not compile' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Phone remote checks failed' }
} finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
