# Phase 0 — browser inside Selene, controller test

## Goal
Prove that an embedded CefSharp browser can live inside a Selene fullscreen overlay
and receive controller input, without touching the user's installed Playnite.

## What is built

- `src/Selene` — fork of mattxslv/Selene (MIT).
  - `theme/App.xaml` — default `SeleneBrowserOverlayVis = Collapsed`.
  - `theme/Views/Main.xaml` — `nav:browser` card, proxy button `SeleneNav_browser`,
    and a full-screen overlay containing `ContentControl x:Name="SeleneBrowser_BrowserSurface"`.
  - `SeleneCore/SeleneCore.cs` — new `cardbrowser` feature flag (default ON).
  - `SeleneCore/src/HomeNav.cs` — card registration, overlay open/close helpers,
    B/Back/Start closing, and suppression of home-row input while the overlay is up.
- `src/SeleneBrowser` — NEW MIT plugin exposing `SeleneBrowser_BrowserSurface`.
  It creates a CefSharp.Wpf `ChromiumWebBrowser` via reflection over Playnite's own
  loaded CefSharp assembly (same trick WebExplorer uses). No NuGet dependencies.
  Public methods: `Navigate(string)`, `GoBack()`, `GoForward()`, `ExecuteScript(string)`.
- `src/PlayniteExtensionsCollection` — WebExplorer source kept for reference only.
- `downloads/WebExplorer_1_3.pext` — prebuilt add-on kept for reference only.

## Build

```
powershell -File src\Selene\tools\build.ps1
powershell -File src\SeleneBrowser\build.ps1
powershell -File scripts\deploy-to-sandbox.ps1
```

Both builds use Windows' bundled `csc.exe` and the installed Playnite SDK DLL
(read-only). No dotnet SDK, Visual Studio, or git required.

## Status — verified automatically (2026-11-17)

- Portable Playnite 10.64 extracted to `Sandbox\Playnite` (config + library + CEF cache all inside it).
- `playnite.log`: both plugins load, no startup errors; controller mappings load once launched with
  the sandbox as the working directory.
- `extensions.log`: `SeleneBrowser: control created`, then
  `SeleneBrowser: navigating to https://example.com/`.
- CEF history contains `https://example.com/Example Domain` — the page really rendered.
- The Browser card is registered via `cardbrowser` (default ON) and `nav:browser`.

A temporary `nomad-autoopen.txt` dev hook was used to trigger the overlay from a script;
it has been removed. The hook remains in `HomeNav.Install()` and is inactive unless that file exists.

## Controller issue and fix (2026-11-18)

Symptom in the sandbox: left stick worked, face buttons did nothing, right stick
generated a flood of bogus DPadDown/TriggerRight events. Logs showed either
`pad: press ignored, Playnite not in front (OpenCode)` or alternating bogus axis
events. Your installed Playnite worked normally with the same controller.

Resolution:
- Restart the controller.
- Launch the sandbox through `scripts/start-sandbox.ps1`, which sets
  `SDL_JOYSTICK_RAWINPUT=0` (the hint Playnite uses for bogus raw-input events).
- Ensure the Playnite window has focus (click it / Guide button) — Selene ignores
  pad buttons when another window is foreground.
- Only one Playnite instance can run: close the installed one before starting the sandbox.

Confirmed working by the user after these steps.

## Phase 1 — working (2026-11-18)

- Browser overlay now has a controller-first command layer: D-pad moves a cyan focus
  outline through visible/topmost page elements (spatial movement, ignores covered
  background links), A clicks, LT/RT scroll, Y cycles bookmarks, X/LB back, RB forward,
  B/Start close. Address is shown top-right.
- Root cause found via logging: `ExecuteScriptAsync` is a CefSharp **extension method**,
  not an instance method — reflection now finds it, so the JS shim actually installs.
- Verified by user: cookie consent dialogs highlight and activate correctly.

## Phase 2 — console rewrite (2026-11-18)

Design and contracts: `docs/NOMAD-SPEC.md`. Old sources: `backup/pre-claude-2026-11-18/`.

- SeleneCore (`HomeNav.cs`): every pad press while an overlay is up goes to the surface's `HandlePad(button)`
  (false = close). `OnShown`/`OnHidden` start and stop the surfaces' timers and pause media. A real keyboard's
  arrows / Enter / Esc work as D-pad / A / B. The theme overlay gives the browser the whole screen.
- Browser: `CefView` (trusted CEF host clicks/keys, no OS cursor), focus mode (spatial nav with a highlight that never
  touches page styles, scroll containers, iframes) + right-stick cursor mode, top bar, on-screen keyboard for every
  text field and the address bar, start page with bookmarks + recent, Start menu (bookmark, zoom, clear data).
- YouTube: FreeTube-style, no account, no ads. InnerTube "local API" like FreeTube (Invidious only as fallback),
  FreeTube data imported read-only (subscriptions, cached feed, history, playlists, searches, quality, SponsorBlock).
  Playback = DASH VP9/Opus through a loopback proxy + shaka-player (one MSE pipeline, quality/speed/captions/audio).

### Automated pad test
`scripts/test-nomad.ps1` builds, deploys and starts the sandbox, keeps it in front, and plays real pad presses
through Playnite's input path via `%TEMP%\selene-dev-keys` (new tokens: `nomad:browser`, `nomad:youtube`,
`nomad:close`), with `shot:<name>` screenshots in `%TEMP%`. Verified 2026-11-18: browser start page → keyboard
search → Google results → open link → back/forward → close; YouTube subscriptions grid → keyboard search with
suggestions → play (sound, 1080p) → controls bar → seek → back to the same card → close.
Not coverable by the script: the right-stick cursor (XInput is polled directly) and how held buttons feel.

## Phase 3 — browser on Edge WebView2 (2026-11-18)

Playnite's CefSharp build has no H.264/AAC, so most video sites (HLS/MP4) could not play in the browser.
- The browser page now uses Edge WebView2 (`EdgeView.cs`, `WebView2CompositionControl` so WPF overlays stay on top),
  with the Microsoft-signed SDK 1.0.4314.35 in `src/SeleneBrowser/lib/webview2` (x86 loader: Playnite runs 32-bit).
  CEF remains the fallback when WebView2 is missing or fails to start. The YouTube screen still uses CEF (VP9 is enough).
- Trusted input via the DevTools protocol. Element fullscreen is emulated in every frame (`EdgeFullscreen.cs`): WebView2
  refuses real fullscreen inside the composition control; the page's player fills the screen, bars hide, B exits.
- Right-stick cursor pushed against an edge scrolls the page.
- `scripts/test-media.html` (open with dev key `nomad:url:file:///.../scripts/test-media.html`): verified H.264, AAC,
  HEVC, VP9, AV1, Opus supported; an H.264 MP4 and a 1080p HLS stream play; iframe player fullscreen + B exit work.

## Phase 4 — YouTube quality of life (2026-11-18)

- Subscriptions: "Shorts: On/Off" chip above the grid (Up from the first row, A toggles; also in the Start menu), saved
  in prefs. Shorts come from the channel RSS `/shorts/` links (FreeTube cache: <= 60 s).
- Watched videos (Nomad/FreeTube history or a saved position) are dimmed; NEW badge for uploads under 24 h; red LIVE badge.
- Livestreams: ANDROID_VR refuses them ("confirm you're not a bot"), so the stream comes from Invidious' HLS; LocalServer
  proxies and rewrites the playlists (YouTube sends no CORS), the page plays it with hls.js. Live is H.264, so the YouTube
  player now runs on Edge WebView2 (CEF fallback says "Live needs Microsoft Edge WebView2"). LIVE / "-m:ss behind",
  seek inside the DVR window, Y = Go live. Live videos never enter history or resume positions.
- Fixed a race: right after switching videos the poll could read the previous page and log the new video as watched.
- Dev keys: `nomad:ytplay:<videoId>` plays a video by id (YouTubeControl.DevPlay).
- Verified in the sandbox: NEW badges, dimming, Shorts toggle, ABC News Live playing at 720p with rewind and Go live.

## Controller test (on the sandbox Playnite only)

1. Start `Sandbox\Playnite\Playnite.exe` (fullscreen mode).
2. On Spotlight, the **Browser** card should be present in the row.
3. Press **A** on it: the browser overlay opens and loads `https://example.com/`.
4. Observe:
   - D-pad / left stick: does the page focus move, or does it scroll?
   - A: does it activate a focused link?
   - B / Start: overlay closes?
5. Note results in this file. They decide the Phase 2 engine:
   - If keys reach the page: keep CefSharp.Wpf + JS spatial-focus shim.
   - If not: switch to CefSharp.OffScreen with a WPF `Image` and JS-injected input.
