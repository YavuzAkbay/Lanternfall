# Updates the YouTube.js copy that the Lanternfall Browser's YouTube web route ships (Browser\lib\ytweb\youtubei.js) and
# keeps it only if real playback works with it. Run it when YouTube videos stop playing, or when FreeTube ships an update:
#
#   tools\update-youtubejs.ps1                    # the newest YouTube.js
#   tools\update-youtubejs.ps1 -Version 18.4.0    # a given version
#   tools\update-youtubejs.ps1 -TestOnly          # only check the copy we have (is it YouTube.js or something else?)
#
# The check (tools\youtubejs\PlaybackCheck.cs) resolves a few videos through the web route alone and reads deep inside a
# video and an audio stream. A failing new version is put back to the previous one. Needs the network; it builds the
# Browser add-on but never releases, commits or touches Playnite.
param(
    [string] $Version = 'latest',
    [string[]] $Videos = @('aqz-KE-bpKQ', 'dQw4w9WgXcQ', 'LXb3EKWsInQ'),
    [switch] $TestOnly
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repo = Split-Path $PSScriptRoot -Parent
$lib = Join-Path $repo 'Browser\lib\ytweb'
$files = @{ bundle = (Join-Path $lib 'youtubei.js'); license = (Join-Path $lib 'LICENSE-youtubei.txt'); version = (Join-Path $lib 'version.txt') }
$current = 'unknown'
if (Test-Path $files.version) { $current = (Get-Content $files.version -Raw).Trim() }
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Test-Playback {
    & (Join-Path $repo 'Browser\build.ps1') | Out-Null
    $work = Join-Path ([IO.Path]::GetTempPath()) 'lanternfall-youtubejs-check'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory $work | Out-Null
    Copy-Item (Join-Path $repo 'Browser\build\*') $work -Recurse -Force
    $sdk = Join-Path $env:LOCALAPPDATA 'Playnite\Playnite.SDK.dll'
    if (-not (Test-Path $sdk)) { $sdk = Join-Path $repo '..\Sandbox\Playnite\Playnite.SDK.dll' }
    Copy-Item $sdk $work -Force
    $csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe'   # 32-bit, like Playnite
    $out = & $csc /nologo /platform:x86 "/out:$work\check.exe" "/r:$work\SeleneBrowser.dll" "/r:$work\Playnite.SDK.dll" (Join-Path $PSScriptRoot 'youtubejs\PlaybackCheck.cs')
    if ($LASTEXITCODE -ne 0) { $out | Where-Object { $_ -match 'error' } | Write-Host; throw 'the playback check did not compile' }
    & (Join-Path $work 'check.exe') @Videos | Write-Host
    return ($LASTEXITCODE -eq 0)
}

function Save-Text([string] $path, [string] $text) { [IO.File]::WriteAllText($path, $text, $utf8) }

function Set-VersionMentions([string] $v) {
    foreach ($doc in (Join-Path $lib 'README.txt'), (Join-Path $repo 'THIRD-PARTY-NOTICES.md')) {
        if (-not (Test-Path $doc)) { continue }
        $text = [IO.File]::ReadAllText($doc)
        $new = [regex]::Replace($text, 'YouTube\.js \d+\.\d+\.\d+', "YouTube.js $v")
        if ($new -ne $text) { Save-Text $doc $new }
    }
}

if ($TestOnly) {
    Write-Host "Checking YouTube playback with the current YouTube.js ($current)..."
    if (Test-Playback) { Write-Host "OK: YouTube.js $current plays."; exit 0 }
    Write-Host "FAILED with YouTube.js $current. Run tools\update-youtubejs.ps1 to try the newest version."
    exit 1
}

$target = $Version
if ($target -eq 'latest') {
    $target = (Invoke-RestMethod 'https://data.jsdelivr.com/v1/package/npm/youtubei.js').tags.latest
    if (-not $target) { throw 'could not find the newest YouTube.js version' }
}
if ($target -eq $current) {
    Write-Host "Already on YouTube.js $current (the newest). Checking that it plays..."
    if (Test-Playback) { Write-Host 'OK: nothing to update.'; exit 0 }
    Write-Host "FAILED with YouTube.js $current, and there is no newer version yet: YouTube changed something YouTube.js has not caught"
    Write-Host 'up with, or something our own Browser\ytweb\resolver.js relies on. Try again in a few days, or look at the log lines above.'
    exit 1
}

Write-Host "Updating YouTube.js $current -> $target"
$backup = Join-Path ([IO.Path]::GetTempPath()) 'lanternfall-youtubejs-backup'
if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
New-Item -ItemType Directory $backup | Out-Null
foreach ($f in $files.Values) { if (Test-Path $f) { Copy-Item $f $backup } }

$raw = Join-Path $backup 'download.js'
Invoke-WebRequest "https://cdn.jsdelivr.net/npm/youtubei.js@$target/bundle/browser.js" -OutFile $raw -UseBasicParsing
$js = [IO.File]::ReadAllText($raw, $utf8)
if ($js.Length -lt 500000 -or $js.IndexOf('Innertube') -lt 0 -or $js.IndexOf('Platform') -lt 0) { throw "the download of YouTube.js $target does not look like its browser bundle" }
$js = [regex]::Replace($js, '(?m)^//# sourceMappingURL=.*\r?\n?', '')
Save-Text $files.bundle $js
Save-Text $files.license ((Invoke-WebRequest "https://cdn.jsdelivr.net/npm/youtubei.js@$target/LICENSE" -UseBasicParsing).Content)
Save-Text $files.version "$target`n"

Write-Host "Checking YouTube playback with YouTube.js $target..."
if (Test-Playback) {
    Set-VersionMentions $target
    Write-Host ''
    Write-Host "OK: YouTube.js $target plays; kept it (Browser\lib\ytweb, version mentions updated)."
    Write-Host 'To ship it: raise Version in Browser\extension.yaml, then release as in docs\releasing.md.'
    exit 0
}

Write-Host ''
Write-Host "YouTube.js $target did NOT pass; putting $current back."
foreach ($f in $files.Values) {
    $saved = Join-Path $backup (Split-Path $f -Leaf)
    if (Test-Path $saved) { Copy-Item $saved $f -Force }
}
Write-Host "Checking YouTube playback with YouTube.js $current again..."
if (Test-Playback) { Write-Host "YouTube.js $current still plays: keep it (the new version breaks something our resolver uses)." }
else { Write-Host "YouTube.js $current fails too: YouTube changed something beyond YouTube.js. Browser\ytweb\resolver.js needs a fix." }
exit 1
