# Nomad — console-style Browser + YouTube for Selene (spec for the rewrite)

Goal: everything in the Browser and YouTube screens works with an Xbox pad only. No keyboard, no mouse, ever.

## Hard build constraints (read first)
- Built by `src\SeleneBrowser\build.ps1` with Windows' bundled `csc.exe` (.NET Framework 4.x, **C# 5 only**):
  no `$"..."` interpolation, no `?.`, no `nameof`, no `=>` expression-bodied members, no `out var`, no tuples, no `is T x` patterns, no local functions.
- Only framework DLLs + `Playnite.SDK.dll`. CefSharp is reached **by reflection only** (Playnite already loads CefSharp 152 / Chromium 152).
  Add any extra framework reference (e.g. `System.Net.Http.dll`) to `build.ps1`'s `$refs`, and add every new `.cs` file to its `$sources`.
- SeleneBrowser.dll is a separate plugin: it cannot reference SeleneCore types (KeyPad, Ui, ...). Build its own WPF components.
- Colors: read Selene palette brushes at runtime with `TryFindResource("SeleneInkBrush")`, `"SeleneInk60Brush"`, `"SeleneAccentBrush"` etc.
  with hard-coded fallbacks, so palettes apply. Focus = accent outline + slight scale, like Selene tiles. Large 10-ft text (>= 20px body, >= 28px titles).
- Writable data goes in the plugin user-data folder (`SeleneBrowserPlugin.DataDir`, set from `GetPluginUserDataPath()`), never `AppDomain.BaseDirectory`.
- Never block the UI thread on network or `Task.Wait`. Use `Task.Run` + `Dispatcher.BeginInvoke`, and `ContinueWith` for CEF evaluate results.

## Pad contract between SeleneCore (HomeNav.cs) and the two surfaces
Both `BrowserControl` and `YouTubeControl` expose exactly:
```
public bool HandlePad(string button)   // true = consumed. false = "I am at top level, close the overlay".
public void OnShown()                  // overlay became visible: start timers / input polling
public void OnHidden()                 // overlay hidden: stop timers, pause media, stop polling
public string Hints { get; }           // current button-hint line (INotifyPropertyChanged), shown by the control itself
```
Button names delivered: `A B X Y Start Back LeftShoulder RightShoulder TriggerLeft TriggerRight DPadUp DPadDown DPadLeft DPadRight LeftStick RightStick`
(left-stick directions are already folded into DPad*). Held buttons arrive as repeated presses every ~80 ms — screens must rate-limit
movement (first repeat after ~350 ms, then every ~90 ms) where needed. **Right-stick directions are NOT delivered**: poll XInput yourself
(`xinput1_4.dll` `XInputGetState`, fall back to `xinput9_1_0.dll`) from a 16 ms DispatcherTimer that runs only between OnShown/OnHidden and
only while `Window.GetWindow(this).IsActive`.
Guide is never delivered (Selene keeps it).

## Shared components (SeleneBrowser plugin)
- `OnScreenKeyboard.cs` — WPF on-screen keyboard overlay for the pad: rows of keys incl. `.com / : - _ @ ?` row for URLs, shift, space,
  backspace, done; suggestions row (strings supplied by caller, e.g. search history / autocomplete). Pad: D-pad moves (wraps), A types,
  X backspace, Y space, LB/RB move text cursor, LT shift toggle, Start = done, B = cancel. API:
  `Open(string title, string initial, Action<string> done, Action cancelled, Func<string, IEnumerable<string>> suggest)`, `bool HandlePad(string)`, `bool IsOpen`.
- `PadInput.cs` — XInput polling (right stick vector + raw buttons), repeat limiter helper.
- `Theme.cs` — brush lookup with fallbacks, focus visuals, shared fonts.

## Browser (`SeleneBrowser.cs`)
- Trusted input: use the CefSharp browser host (`chromium.GetBrowser().GetHost()` via reflection, or the `GetBrowserHost` extension)
  and its `SendMouseClickEvent`, `SendMouseMoveEvent`, `SendMouseWheelEvent`, `SendKeyEvent` — real user gestures (audio unlock, popups,
  hover menus). No `SetCursorPos`/`mouse_event` OS cursor hacks.
- Two modes: **Focus mode** (default): D-pad spatial navigation between interactive elements, drawn with an injected absolutely-positioned
  highlight `<div>` (never overwrite page `style.outline`); includes elements inside scrollable containers and same-origin iframes;
  scrolls the nearest scrollable ancestor; A = trusted click at element center. **Cursor mode**: right stick moves a visible WPF-drawn
  cursor over the page (with acceleration), A clicks, RT/LT scroll wheel. Moving the right stick switches to cursor mode; D-pad switches back.
- When the focused element is a text input/textarea/contenteditable, A opens the on-screen keyboard, then text is typed with trusted
  `SendKeyEvent` char events (Enter on Done).
- Top bar (WPF, over the page): Back / Forward / Reload / Address (A opens keyboard: URL or search → Google... prefer DuckDuckGo-free
  default: use `https://www.google.com/search?q=`) / Home / Zoom. View button (Back) or Y toggles focus between page and top bar.
- Home page: a local console-style start page (served as `data:` HTML or rendered in WPF) with large bookmark tiles (YouTube TV-free:
  link to the YouTube screen is not needed here), user bookmarks (add current page with a menu action), recent history. Persist bookmarks
  + history in DataDir JSON.
- Pad map: D-pad move, A select, B back (history back; at the start page with nothing to go back → return false = close), X reload,
  Y top bar, LB/RB back/forward, LT/RT page scroll (repeat while held), right stick = cursor, L3 zoom out, R3 zoom in, Start = menu
  (Add bookmark, Zoom, Clear data, Close). `Hints` updates per mode.
- JS shim installed once per page load via `FrameLoadEnd` (reflection event subscribe) or a cheap timer **only while shown**.
- Popups/new windows open in the same view. Downloads ignored. Layout fills the overlay (no fixed heights).

## YouTube (`YouTube*.cs`) — FreeTube-style, no Google account, no ads
- Backend = InnerTube directly ("local API", like FreeTube), Invidious only as fallback:
  - Player/streams: POST `https://www.youtube.com/youtubei/v1/player?prettyPrint=false` with client
    `{"clientName":"ANDROID_VR","clientVersion":"1.65.10","deviceMake":"Oculus","deviceModel":"Quest 3","androidSdkVersion":32,"osName":"Android","osVersion":"12L","hl":"en","gl":"US"}`
    and User-Agent `com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip`.
    Verified 2026-11-18: returns plain (unciphered) `url`s for all itags incl. 2160p VP9 + Opus, with `initRange`/`indexRange`, playable from the user's IP.
    Captions in `captions.playerCaptionsTracklistRenderer.captionTracks[].baseUrl` (append `&fmt=vtt`).
  - Search/browse/channel/next(related): WEB client `{"clientName":"WEB","clientVersion":"2.20251101.00.00","hl":"en"}` on
    `/youtubei/v1/search`, `/browse` (channel `browseId` = UC id, params for Videos tab), `/next`. Parse `videoRenderer`,
    `richItemRenderer`, `gridVideoRenderer`, `lockupViewModel` defensively. Search suggestions: `https://suggestqueries-clients6.youtube.com/complete/search?client=youtube&ds=yt&q=`.
  - Fallback: Invidious API on a list of instances (start with `https://yt.chocolatemoo53.com`, refresh list from
    `https://api.invidious.io/instances.json?sort_by=health`, api=true), with `?local=true`.
- googlevideo sends no CORS headers, so playback goes through a **loopback HTTP server** (`LocalServer.cs`, TcpListener on 127.0.0.1,
  random free port, no admin needed) that serves: `/player?v=ID` (player page), `/manifest/ID.mpd` (DASH generated from the formats:
  VP9/AV1 webm video + Opus audio, SegmentBase with indexRange/Initialization), `/s/<token>` proxy with Range pass-through for stream
  URLs (token maps to the real URL server-side; never put raw URLs in the page), `/cc/<token>.vtt` captions.
  Player page uses shaka-player from `https://cdn.jsdelivr.net/npm/shaka-player@4.11.7/dist/shaka-player.compiled.js` (pinned).
  ABR on, cap to the screen height and the user's FreeTube `defaultQuality` setting; one player = one MSE pipeline (no separate audio element).
- FreeTube import (all NeDB = newline-delimited JSON; later lines override earlier ones with the same `_id`; `$$deleted` lines delete):
  `%APPDATA%\FreeTube\profiles.db` (subscriptions with `id`, `name`, `thumbnail`), `history.db` (sort by `timeWatched` desc,
  `watchProgress` = resume position), `subscription-cache.db` (per-channel cached `videos` — instant Subscriptions feed),
  `playlists.db`, `search-history.db`, `settings.db` (`defaultQuality`, `useSponsorBlock`). Read-only: never write FreeTube's files.
  Nomad's own history/positions/prefs in DataDir.
- Subscriptions refresh: channel RSS `https://www.youtube.com/feeds/videos.xml?channel_id=` with max 3 parallel + backoff on 429, merged
  with subscription-cache.db; cache in DataDir. Show the cached feed instantly, refresh in background.
- SponsorBlock (if FreeTube setting on): `https://sponsor.ajay.app/api/skipSegments?videoID=ID&categories=["sponsor","selfpromo"]` — auto-skip in player.
- UI (WPF, console style, like YouTube on TV): left rail (Home / Subscriptions / Search / History / Playlists / Channels) + content area
  with a thumbnail **grid** (4 per row at 1080p, 16:9 thumbs with duration badge, title 2 lines, channel · age · views). Async thumbnail
  loading with `DecodePixelWidth`, virtualization-ish (only build what is needed; append on scroll near end = load more / continuation).
  Video page before/around the player: title, channel, related videos below. Channel page: header + videos grid.
- Player (CEF page full-bleed inside the YouTube surface) with a **WPF controls overlay** drawn by YouTubeControl: auto-hides after 3 s,
  any press shows it. Pad: A play/pause (or activate focused control when the bar is focused), D-pad left/right seek ±10 s (hold = faster),
  LT/RT ±60 s, D-pad up/down show bar and move between seek bar and buttons row (Quality, Speed, Captions, Audio track, Related),
  X captions toggle, Y fullscreen/theater toggle, LB/RB previous/next in queue, B = hide bar / exit player back to the list, Start = menu.
  Resume from saved position; save position every 5 s and on exit; mark watched in Nomad history.
- Player ↔ C# communication: poll `EvaluateScriptAsync` (via reflection, async continuation) for a small JSON state
  (`{t,d,paused,buffering,quality,qualities,captions,audioTracks,ended}`) every 500 ms while playing — no `document.title` side channel.
- Every network failure shows a friendly message on screen with "A Retry / B Back", never a dead screen.
