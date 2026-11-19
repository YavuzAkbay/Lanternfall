# End-to-end pad test on the ISOLATED sandbox Playnite (never the installed one).
# Builds + deploys, opens the Browser or YouTube overlay at startup, plays a scripted sequence of real pad presses
# through Playnite's controller input path (Selene's %TEMP%\selene-dev-keys hook), takes screenshots
# (%TEMP%\selene-shot-<name>.png), then closes Playnite and prints the relevant log lines.
#   powershell -File tools\sandbox\test-overlays.ps1 -Open youtube -Keys "9000 shot:yt-home 800 pad:DPadRight 600 shot:yt-moved" -Seconds 40
param(
    [ValidateSet('browser', 'youtube', 'none')] [string] $Open = 'browser',
    [string] $Keys = '9000 shot:start',
    [int] $Seconds = 30,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sandbox = if ($env:LANTERNFALL_SANDBOX) { $env:LANTERNFALL_SANDBOX } else { Join-Path (Split-Path $repo -Parent) 'Sandbox\Playnite' }

if (Get-Process -Name 'Playnite.FullscreenApp', 'Playnite.DesktopApp' -ErrorAction SilentlyContinue) {
    throw 'A Playnite instance is running. Close it first (only one instance can run, and this test must not touch your installed Playnite).'
}
if (-not $NoBuild) {
    & powershell -NoProfile -File (Join-Path $repo 'tools\build.ps1') | Select-Object -Last 1
    & powershell -NoProfile -File (Join-Path $repo 'Browser\build.ps1') | Select-Object -Last 1
    & powershell -NoProfile -File (Join-Path $PSScriptRoot 'deploy-to-sandbox.ps1') | Select-Object -Last 1
}

$hook = Join-Path $sandbox 'nomad-autoopen.txt'
$keysFile = Join-Path ([IO.Path]::GetTempPath()) 'selene-dev-keys'
Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'selene-shot-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
if ($Open -eq 'none') { Remove-Item $hook -ErrorAction SilentlyContinue } else { Set-Content $hook $Open }
Set-Content $keysFile $Keys

$logs = 'playnite.log', 'extensions.log' | ForEach-Object { Join-Path $sandbox $_ }
$start = @{}; foreach ($l in $logs) { $start[$l] = if (Test-Path $l) { (Get-Item $l).Length } else { 0 } }

$env:SDL_JOYSTICK_RAWINPUT = '0'
$p = Start-Process -FilePath (Join-Path $sandbox 'Playnite.FullscreenApp.exe') -WorkingDirectory $sandbox -PassThru
# Injected presses only count while Playnite is the foreground window: keep it in front during the run.
Add-Type -Namespace Nomad -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
'@ -ErrorAction SilentlyContinue
function Front {
    if ($p.HasExited) { return }
    $p.Refresh(); $h = $p.MainWindowHandle
    if ($null -eq $h) { return }
    if ($h -eq [IntPtr]::Zero -or [Nomad.Win]::GetForegroundWindow() -eq $h) { return }
    [Nomad.Win]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [Nomad.Win]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)   # Alt tap lets us take the foreground
    [Nomad.Win]::SetForegroundWindow($h) | Out-Null
}
try { for ($i = 0; $i -lt $Seconds; $i++) { Start-Sleep -Seconds 1; Front } }
finally {
    Remove-Item $keysFile, $hook -ErrorAction SilentlyContinue
    if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; if (-not $p.WaitForExit(15000)) { Stop-Process -Id $p.Id -Force } }
    Get-Process -Name 'Playnite.FullscreenApp' -ErrorAction SilentlyContinue | Stop-Process -Force
}

foreach ($l in $logs) {
    if (-not (Test-Path $l)) { continue }
    $fs = [IO.File]::Open($l, 'Open', 'Read', 'ReadWrite'); $fs.Seek($start[$l], 'Begin') | Out-Null
    $text = (New-Object IO.StreamReader($fs)).ReadToEnd(); $fs.Close()
    "===== $(Split-Path $l -Leaf) (new lines, filtered)"
    $text -split "`r?`n" | Where-Object { $_ -match 'SeleneBrowser|nomad|browser|youtube|dev key|dev shot|ERROR|Exception' } | Select-Object -Last 120
}
"===== screenshots"
Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'selene-shot-*.png' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
