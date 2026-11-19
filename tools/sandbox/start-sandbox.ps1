# Launches ONLY the isolated portable Playnite in fullscreen mode (..\Sandbox\Playnite next to the repo, or LANTERNFALL_SANDBOX).
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = if ($env:LANTERNFALL_SANDBOX) { $env:LANTERNFALL_SANDBOX } else { Join-Path (Split-Path $repo -Parent) 'Sandbox\Playnite' }
if (-not (Test-Path -LiteralPath (Join-Path $sandbox 'Playnite.FullscreenApp.exe'))) { throw 'Sandbox not deployed' }
# SDL hint that Playnite uses to avoid bogus raw-input events from Bluetooth Xbox controllers.
$env:SDL_JOYSTICK_RAWINPUT = '0'
Start-Process -FilePath (Join-Path $sandbox 'Playnite.FullscreenApp.exe') -WorkingDirectory $sandbox
"Started sandbox Playnite from $sandbox (SDL raw input disabled)"
