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
- YouTube: FreeTube-style, no account. InnerTube "local API" like FreeTube (Invidious only as fallback),
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

## Phase 5 — links stay in the theme, browser tabs (2026-11-22)

- Every http(s) link the theme opens goes through `HomeNav.OpenInThemeBrowser` and lands in the in-theme browser overlay;
  `OpenOutside` is the single router (launcher schemes and local files still hand off to Windows).
- News card / News page / legacy list use the reusable `news` tab; Store items prefer `WebLink`, and the Store list now
  opens each store's website (all stores shown, row scales down on narrow displays). Store/News screens stay up behind
  the overlay so B returns to them.
- `BrowserControl.OpenUrl(url, reuseKey)` avoids the disposed-engine race by calling `EnsureView()` first.
- Browser now has up to 5 real tabs (`BrowserTab.cs`): one WebView2/CEF engine per tab, background tabs idle + muted,
  engines disposed after 45 s hidden and recreated from their saved URLs. LB/RB switch tabs from any browsing mode
  (history stays on the top bar's Back/Forward and B); the top bar has New/Close tab, the Start menu also has Next tab,
  and Edge pop-ups open as tabs. Overlay keyboard input and focus restoration were fixed for
  screens behind the overlay.
- Dev keys: `nomad:news`, `nomad:store` open those screens directly; `test-overlays.ps1` has `-SampleProcesses`.
- Status: both builds and all code suites pass (the `library` suite needs an SDK at a local `selene-fresh` folder
  and fails in this environment for that reason only). Sandbox pad testing of the final repo was paused at the
  maintainer's request; the earlier prototype was verified for news/store links and flat process/memory usage.

## Controller-only IGDB metadata download (2026-11-22)

- New first row in fullscreen **Game options > Edit details**: "Download metadata (IGDB)". The whole flow stays in
  fullscreen: `IgdbSearchScreen` (query prefilled with the game's name, covers, `KeyPad` to edit the query) → pick a
  game → `IgdbImageScreen` (wide artwork/screenshots with resolutions, "Keep current background") → A applies and saves.
  B cancels at any point; nothing is written until the final A.
- Playnite's own manual metadata dialogs throw in fullscreen (`FullscreenDialogs.ChooseItemWithSearch/ChooseImageFile`),
  so this uses the same keyless backend the built-in add-on does (`api2.playnite.link/api/igdb/search` and
  `/game/{id}`), already used by `GameMedia.cs`. No dependency on IGDBMetadata.dll and no API key needed.
- Fills the fields IGDB provides (name, description, release date, scores, genres, developers, publishers, features,
  age ratings, series, links, cover and chosen background); leaves platforms/tags/categories/your score alone.
- New files: `SeleneCore/src/IgdbMetadataClient.cs` (network + parsing, no SDK/WPF refs), `SeleneCore/src/IgdbMetadataScreen.cs`
  (both screens); offline unit suite `tools/tests/igdbmeta.ps1` + `IgdbMeta.Check.cs`. Dev key `screen:igdb[:Game_Name]`
  opens the search directly in the sandbox.
- Status: build and all offline suites pass; `library` still needs the missing `selene-fresh` SDK path (it passes when
  pointed at the sandbox). Live pad testing was done by the maintainer in the sandbox.
- Fix from that test: searching again when A picked a result. `WantsText` was on for the whole screen, so Playnite's
  keyboard copy of pad A became Enter → "Start" (LoginScene.OnKey) and re-ran the search. Text input is now enabled only
  while the keyboard is up, late A copies can't finish typing early, and "Search again" moved from Start to **Y**.
- Cover choice added after the maintainer's feedback: the image step is now two screens. A on the background grid stores
  the background and opens a cover grid ("Keep current cover", the single official `cover_expanded`, plus portrait
  artwork/screenshots where the backend has them); A there is the one and only database write. B steps back between the
  two grids and keeps both picks. Playnite's IGDB backend exposes only one cover per game — no multi-cover list exists —
  so the alternatives are portrait images from the same game's artwork/screenshot sets.
- Spotlight now shows news about the SELECTED game in that same bottom slot when Steam news is available: `GameNews.cs`
  resolves a Steam AppID (override → Steam GameId → name lookup), reads the keyless `ISteamNews/GetNewsForApp` API
  (`SteamGameNews.cs`), caches results for 12 h, and publishes up to 3 tiles (thumbnail, title, source/date). Community
  announcements come first. With no AppID or no articles the screenshot strip stays exactly as before. A on a tile opens
  the article in the in-theme browser with the same "news" reuse key as the What's new section. Setting: `gamenews`.
- Fixes from the maintainer's first test: non-English Steam feeds (Cyrillic/CJK titles and sources) are skipped; the full
  article body is fetched so its `[img]`/`<img>`/preview-video picture can be used (Steam's `thumbnail` field is nearly
  always empty), with the game's own art as a last fallback; the picture cache was versioned so old empty entries are
  ignored. A on the tiles now has three routes (pad hook, keyboard Enter, and a real ButtonEx Click handler), focus entry
  walks the live hub and checks that focus really moved, because Playnite silently swallows pad A on a button with no Click.

## Phase — the empty bottom slot under the web cards

The Browser and YouTube cards used the same bottom slot as a selected game (screenshots / Steam news) but left it blank.
`SeleneCore/src/FeedCards.cs` now switches two new three-tile grids in `theme/Views/Main.xaml` on selection: newest videos
from subscribed channels for YouTube (thumbnail, channel and age, `NEW` since the last visit) and the user's own places
for the Browser (bookmarks first, recent history to fill, drawn letter tiles — no network favicons). The data is shaped
in `Browser/CardFeed.cs` (pure, offline-tested in `tools/tests/CardFeed.Check.cs`) and drawn/published by
`Browser/CardFeedArt.cs` through `CardInfo`'s existing background refresh, so nothing new crosses the plugin boundary
but plain resources. A on a tool opens it: the video starts through `HomeNav.OpenYouTubeVideo` (the same reflection path
as the dev key) and a site opens in the in-theme browser with a `cardfeed` reuse key. Focus/Down, hints, the focus slide
and a `cardpreviews` setting were wired the same way as game news. Kept in new files with small HomeNav hooks so the
upstream Selene merge stays easy.

## Phone keyboard (2026-11-25)

View on the Browser / YouTube on-screen keyboard shows a QR card; the phone opens a small page from the PC and types into the
keyboard's field live, both ways. `Browser/PhoneKeyboard.cs` (no WPF: address and client rules, request parsing, `PhoneSession` with its
routes, tokens, limits and a loopback-testable socket server), `Browser/PhonePage.cs` (the phone page, one HTML string, no outside
requests), `Browser/PhoneFirewall.cs` (asks Windows Firewall over COM whether Playnite's fullscreen program can be reached; the one-time
setup adds one inbound TCP rule for LocalSubnet through UAC, only when the user presses it), `Browser/QrCode.cs` (copy of Core's).
`OnScreenKeyboard` got the card, `Open(..., secret, multiline)` and a `Back` branch; `BrowserSettings` got `PhoneKeyboard` (on) and
`PhonePasswords` (off). Dev key `nomad:phone` opens the address keyboard.

- Why a LAN server and not the cloud relay Report-a-problem uses: typed text, passwords included, should not pass through a third party.
- The listener binds to ONE private address (not every adapter), answers only private-range clients, every path carries a random
  24-hex token, 20 wrong guesses or 5 idle minutes end it. It is plain HTTP; that is why passwords default to the TV.
- This PC's network was "Public" with no rule for the fullscreen program: Windows would drop the phone's connection (and its "Allow
  access?" box needs a mouse). Hence the firewall check and the setup row in the browser settings.
- Tests: `tools/tests/phonekeyboard.ps1` (offline, includes a real loopback round trip). The keyboard control was also driven in a
  throwaway WPF harness (View, phone types, pad types, Done) and the page's script in a real browser at phone size. Not done: a real
  phone on the real network, and the settings card's look with the three new rows.

### Phone keyboard in Core's own keyboards (2026-11-25)

Find a game (`SearchScreen`), the IGDB search (`IgdbSearchScreen`) and Add feed (`AddNewsFeedScreen`) take View too. Core cannot reference the
Browser add-on, so `Browser/PhoneBridge.cs` is the seam: a public `PhoneBridge.Start(title, initial, multiline, secret, accent, text, done, cancelled,
connected)` taking only strings and `System` delegates, returning a `PhoneHandle` (`Url`, `Message`, `Hint`, `SetText`, `Close`); the settings and the
firewall hint stay in the add-on. `SeleneCore/src/PhoneType.cs` finds it by reflection (like `OpenInThemeBrowser`), draws the QR card on the right of
the screen and keeps the screen's field and the phone's box in step; each screen only adds a `PhoneType`, a `Back` branch, a `SetQuery`/`SetUrl`, a
`Changed()` push after pad typing and a `Hidden()` close. An older Browser add-on makes View say it needs the newer one. The digit-only MediaId
screen and the report screen (it has its own cloud-relay QR) were left alone. Checked in a throwaway harness (real `PhoneType` + real add-on over
loopback: phone sees the field, phone text lands in the screen, pad text reaches the phone, Done runs the screen's action).

## Browser page tools (2026-11-25)

Tier 2 of the brainstorm, minus the parts that did not earn their place on a pad (see below). All in `Browser/`:

- `BrowserControl.Tools.cs` (partial class): the Start menu is rebuilt there (`OpenMenu` probes the page for a link under the highlight or cursor,
  then `ShowMainMenu`; `ShowPageTools` is a submenu; `MenuEntry.Keep` means "opens another menu"). Find in page (`window.find`, the on-screen keyboard,
  A / Y / X / B while it is active), reader mode, History and Downloads panels, the tab switcher, per-site zoom and phone version.
- `ListPanel.cs` (a glass card of pill rows: History, Downloads, the download question) and `TabSwitcher.cs` (cards with a picture of each page).
- `ReaderScript.cs`: the reader and find scripts as pure strings (a text-container scorer; copies headings, paragraphs, lists, quotes and pictures
  into a fixed layer and hides the page; `window.__lfReader.off()` restores it).
- `EdgeView.Extras.cs` (EdgeView is now `partial`): downloads (`DownloadStarting` is held with a deferral until the screen answers; a minute without an
  answer refuses it; the video player still refuses), a phone user agent through `Emulation.setUserAgentOverride` (with client hints) that a remembered site
  switches to before the request (the request is cancelled and the same address opened again; a cancelled navigation no longer raises `LoadEnd`), and
  `CapturePreview` for the tab pictures (taken after a page finishes loading and when the switcher opens).
- `BrowserSettings`: `SiteZoom`, `MobileSites`, `DownloadsAllowed`. `Downloads.cs`: the request / item types and the pure helpers.
- The page script got `link()` (the address under the highlight, or under a point).

Left out on purpose: numbered link hints (the pad already has spatial focus and a cursor; typing numbers on a pad is slower than both), bookmark folders
(the start page and bookmarks list are short; folders add a screen without a clear need), scroll momentum (a feel change that needs the pad in hand, not a
feature). The "hold X" link menu became the link entries at the top of Start: no hold detection, and it also works from the cursor.

Checked: `tools\tests\browser-tools.ps1` (pure helpers and scripts, offline) and, in a throwaway window hosting the real add-on and a real Edge view, the reader
script on an article page (menus, sidebar and footer dropped, page restored), find (count, selection, hostile query), the phone user agent (page, client
hints, server header, per-site switching), a PNG of the page, downloads refused and accepted (3000 bytes saved), and the whole Start menu flow on a real http
page (link entries, Page tools and back, Open link in new tab, zoom memory in settings.json). Not checked: the pad in the real app, and the look on a
real theme (the look was rendered with the fallback palette).

## Browser scrolling (2026-11-25)

Scrolling used to be wheel events sent from the host: LT / RT repeated a 240-unit wheel kick (first repeat after 350 ms, then every 90 ms), the cursor pushed
at an edge sent wheel events whether or not the page could move, and the left stick only produced D-pad presses (focus moves). Now:

- `PageScroller.cs` (no WPF) turns the left stick (`PadInput.LeftX/LeftY`, new), the triggers (analog depth) and the edge push into a speed in viewport heights
  per second, with a response curve, an axis lock (a stick pushed mostly down does not drift sideways) and an ease in / glide out (70 ms). It sends the speed to
  the page a few times a second (change or 150 ms heartbeat); one stop at the end.
- The page script (`__nomadNav.scroll(fx, fy, ax, ay)`) scrolls once per `requestAnimationFrame` with `scrollBy({behavior:'instant'})`, so CSS smooth-scroll pages
  cannot fight it, keeping the sub-pixel remainder. It scrolls the nearest scroller under the point (the highlight, the cursor, or the middle) that still has
  room, then its parents, then the page; at an end it asks nothing of the browser for a moment (a short back-off, so a page that grows, infinite scroll, is found).
  It stops by itself 450 ms after the last message. It reports what really moved (`lfscroll:dx:dy` web message -> `EdgeView.ScrollMoved`), which feeds the
  auto-hiding bars; engines without that channel get a guess from the speed.
- The left stick is also reported by Playnite as D-pad presses. `BrowserControl.FromLeftStick` takes a fresh XInput sample: a press with the real D-pad bit down moves
  the focus, otherwise (and the stick pushed, or let go less than 150 ms ago) it is dropped. Keyboard arrows and pads XInput cannot see are D-pad presses as before;
  without an XInput pad LT / RT keep the old kick scrolling.

Checked in a real Edge view with the real `PageScroller` and page script (throwaway harness): no frame stands still while scrolling, the step per frame is even (sd/mean
0.20), exactly zero scroll calls in 1.5 s of pushing at the top, bottom, left and right ends, an inner panel first and then the page, the stop when the host goes quiet,
on a page with `scroll-behavior: smooth`. Offline: `tools\tests\browser-tools.ps1` (curves, axis lock, message rate, one stop). Not checked: the pad feel in the app
(speed 1.35 viewport heights per second at full push; `PageScroller.MaxSpeed` and the curve are the knobs).
