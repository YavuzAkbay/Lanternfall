# Deploys the Lanternfall theme + Lanternfall Core + Lanternfall Browser into an ISOLATED portable Playnite.
# It never writes to an installed Playnite (%LOCALAPPDATA%\Playnite / %APPDATA%\Playnite).
# The sandbox is a portable Playnite extracted next to this repo (..\Sandbox\Playnite), or wherever LANTERNFALL_SANDBOX points.
# Build first: tools\build.ps1 and Browser\build.ps1.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = if ($env:LANTERNFALL_SANDBOX) { $env:LANTERNFALL_SANDBOX } else { Join-Path (Split-Path $repo -Parent) 'Sandbox\Playnite' }
$themeId = ((Get-Content (Join-Path $repo 'theme\theme.yaml')) -match '^Id:' | Select-Object -First 1).Substring(3).Trim()
$coreId = ((Get-Content (Join-Path $repo 'SeleneCore\extension.yaml')) -match '^Id:' | Select-Object -First 1).Substring(3).Trim()
$browserId = ((Get-Content (Join-Path $repo 'Browser\extension.yaml')) -match '^Id:' | Select-Object -First 1).Substring(3).Trim()

if (-not (Test-Path -LiteralPath (Join-Path $sandbox 'Playnite.FullscreenApp.exe'))) { throw "Portable Playnite not found at $sandbox" }

$themeDest = Join-Path $sandbox "Themes\Fullscreen\$themeId"
New-Item -ItemType Directory -Force $themeDest | Out-Null
Copy-Item (Join-Path $repo 'theme\*') $themeDest -Recurse -Force
"theme -> $themeDest"

$extDest = Join-Path $sandbox 'Extensions'
# folders of the same add-ons under their old (pre-Lanternfall) ids would load twice
foreach ($old in 'SeleneCore', 'SeleneBrowser') {
    $p = Join-Path $extDest $old
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; "removed old $p" }
}
foreach ($pair in @(@($coreId, (Join-Path $repo 'SeleneCore\obj\stage')), @($browserId, (Join-Path $repo 'Browser\build')))) {
    $dest = Join-Path $extDest $pair[0]
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $pair[1] '*') $dest -Recurse -Force
    "add-on -> $dest"
}

# Portable Playnite keeps config.json / fullscreenConfig.json next to its EXE.
# Start from the installed config (read-only) so the sandbox looks familiar, then isolate it.
$installedConfig = Join-Path $env:APPDATA 'Playnite\config.json'
$installedFull = Join-Path $env:APPDATA 'Playnite\fullscreenConfig.json'
$app = @{}
if (Test-Path -LiteralPath $installedConfig) {
    try { $app = Get-Content -LiteralPath $installedConfig -Raw | ConvertFrom-Json -ErrorAction Stop } catch { $app = @{} }
}
$app | Add-Member -NotePropertyName 'FirstTimeWizardComplete' -NotePropertyValue $true -Force
$app | Add-Member -NotePropertyName 'StartInFullscreen' -NotePropertyValue $true -Force
$app | Add-Member -NotePropertyName 'DatabasePath' -NotePropertyValue (Join-Path $sandbox 'library') -Force
foreach ($key in 'CheckForProgramUpdates', 'CheckForAddonUpdates', 'CheckForLibraryUpdates', 'CheckForEmulatedLibraryUpdates') {
    $app | Add-Member -NotePropertyName $key -NotePropertyValue 0 -Force
}
$app | Add-Member -NotePropertyName 'AutoBackupEnabled' -NotePropertyValue $false -Force
$app | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $sandbox 'config.json') -Encoding UTF8

$full = @{}
if (Test-Path -LiteralPath $installedFull) {
    try { $full = Get-Content -LiteralPath $installedFull -Raw | ConvertFrom-Json -ErrorAction Stop } catch { $full = @{} }
}
$full | Add-Member -NotePropertyName 'Theme' -NotePropertyValue $themeId -Force
$full | Add-Member -NotePropertyName 'EnableGameControllerSupport' -NotePropertyValue $true -Force
$full | Add-Member -NotePropertyName 'GuideButtonFocus' -NotePropertyValue $true -Force
# the installed config may pin a monitor number that is another screen here: the sandbox opens on the primary display
$full | Add-Member -NotePropertyName 'UsePrimaryDisplay' -NotePropertyValue $true -Force
$full | Add-Member -NotePropertyName 'GuideButtonFocusOnlyWhenNoGameRunning' -NotePropertyValue $false -Force
$full | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $sandbox 'fullscreenConfig.json') -Encoding UTF8
"config -> sandbox root (updates off, own library DB, start in fullscreen)"

# Carry the SteamGridDB key and the art-download permission into the sandbox so
# Look > Get square art for all games can run there without pasting the key again.
$pluginGuid = $coreId -replace '^[^_]+_', ''
$installedPluginConfig = Join-Path $env:APPDATA ('Playnite\ExtensionsData\' + $pluginGuid + '\config.json')
$sandboxPluginConfig = Join-Path $sandbox ('ExtensionsData\' + $pluginGuid + '\config.json')
if (Test-Path -LiteralPath $installedPluginConfig) {
    try {
        $installedPlugin = Get-Content -LiteralPath $installedPluginConfig -Raw | ConvertFrom-Json -ErrorAction Stop
        $gridKey = $null
        if ($installedPlugin.Values -and $installedPlugin.Values.'steamgriddb-key') { $gridKey = $installedPlugin.Values.'steamgriddb-key' }
        $sandboxPlugin = @{}
        if (Test-Path -LiteralPath $sandboxPluginConfig) { $sandboxPlugin = Get-Content -LiteralPath $sandboxPluginConfig -Raw | ConvertFrom-Json -ErrorAction Stop }
        if (-not $sandboxPlugin.Values) { $sandboxPlugin | Add-Member -NotePropertyName 'Values' -NotePropertyValue @{} -Force }
        if ($gridKey) { $sandboxPlugin.Values | Add-Member -NotePropertyName 'steamgriddb-key' -NotePropertyValue $gridKey -Force }
        $sandboxPlugin.Values | Add-Member -NotePropertyName 'consent-artdownload' -NotePropertyValue 'explicit-media-v1' -Force
        if (-not $sandboxPlugin.TurnedOn) { $sandboxPlugin | Add-Member -NotePropertyName 'TurnedOn' -NotePropertyValue @() -Force }
        if ($sandboxPlugin.TurnedOn -notcontains 'artdownload') { $sandboxPlugin.TurnedOn += 'artdownload' }
        New-Item -ItemType Directory -Force (Split-Path $sandboxPluginConfig) | Out-Null
        $sandboxPlugin | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $sandboxPluginConfig -Encoding UTF8
        "square art key + art consent -> $sandboxPluginConfig"
    } catch { Write-Warning "square art settings not copied: $_" }
}

"DONE. Start $sandbox\Playnite.FullscreenApp.exe"
