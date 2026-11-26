# Build and run every local suite in a separate process; never launch the user's Playnite.
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$results = @()
$shell = (Get-Process -Id $PID).Path
function Check([string]$Name, [string]$File) {
    & $shell -NoProfile -ExecutionPolicy Bypass -File $File | Out-Host
    $script:results += [pscustomobject]@{ Check = $Name; Result = $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) }
    return ($LASTEXITCODE -eq 0)
}
if (-not $SkipBuild) {
    $ok = Check 'Windows build' (Join-Path $PSScriptRoot 'build.ps1')
    if (-not $ok) { $results | Format-Table; exit 1 }
}
foreach ($suite in 'check', 'igdbmeta', 'gamenews', 'cardfeed', 'phonenet', 'phoneremote', 'browser-tools', 'browser-codec', 'browser-expiry', 'browser-caption', 'browser-tabs', 'bugs', 'interaction', 'library', 'security', 'backgroundchanger') {
    $null = Check $suite (Join-Path $PSScriptRoot "tests/$suite.ps1")
}
& node (Join-Path $PSScriptRoot 'tests/trailer-loop.cjs')
$results += [pscustomobject]@{ Check = 'Trailer canvas (node)'; Result = $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) }
$results | Format-Table -AutoSize
if (@($results | Where-Object Result -eq 'FAIL').Count) { exit 1 }
Write-Host 'All checks passed.'
