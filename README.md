<p align="center">
  <img src="docs/logo/lanternfall-banner.png" alt="Lanternfall" width="100%">
</p>

<p align="center">
  <b>A calm, console-style fullscreen theme for <a href="https://playnite.link">Playnite</a>, with a web browser and YouTube built for the couch.</b><br>
  Everything works with a controller: no keyboard, no mouse.
</p>

<p align="center">
  <a href="#install">Install</a> ·
  <a href="#whats-different">What's different</a> ·
  <a href="docs/guide/README.md">Guide</a> ·
  <a href="docs/lanternfall/UPSTREAM.md">Staying in sync with Selene</a> ·
  <a href="#credits-and-license">Credits</a>
</p>

Lanternfall is a fork of [Selene](https://github.com/mattxslv/Selene) by Matthew Silva. It keeps everything Selene
does (Spotlight, profiles, the store, captures, achievements, the launch experience, 23 color themes) and adds two
screens of its own. It is not made or supported by Selene's author: please report Lanternfall problems
[here](../../issues), not to Selene.

## What's different

- **Browser.** A full web browser in a fullscreen overlay, driven entirely by the pad: D-pad moves between links and
  buttons, the right stick drives a cursor, an on-screen keyboard appears for every text field and the address bar.
  Bookmarks, recent pages, zoom, and video sites play in real fullscreen (Microsoft Edge WebView2).
- **YouTube.** A TV-style YouTube screen with no account and no ads: subscriptions, search with suggestions, history,
  playlists, channels, and a player with quality, speed, captions and audio tracks. It can import your
  [FreeTube](https://freetubeapp.io) subscriptions, history and settings (read-only) and skips sponsor segments with
  [SponsorBlock](https://sponsor.ajay.app) when FreeTube has it on. Livestreams play too.
- **Its own name and mark.** Lanternfall installs next to Selene without clashing (different add-on ids) and checks
  its own releases for updates.

Both screens are Spotlight cards: switch them off in **Settings → Spotlight** if you don't want them.

## Install

1. Download `Lanternfall.pthm`, `LanternfallCore.pext` and `LanternfallBrowser.pext` from the
   [latest release](../../releases/latest).
2. Open each file (or drag it onto Playnite) and confirm. Restart Playnite.
3. Fullscreen mode → **Menu → Playnite settings → Appearance → Theme → Lanternfall**.

Playnite 10 or newer, Windows 10 / 11, a controller recommended. The browser uses Microsoft Edge WebView2, which
Windows 10 / 11 already include.

## Building from source

```powershell
tools\build.ps1        # Lanternfall Core -> SeleneCore\obj\stage (the C# compiler that ships with Windows)
Browser\build.ps1      # Lanternfall Browser -> Browser\build
```

A portable Playnite next to the repo (`..\Sandbox\Playnite`) is a safe place to try it without touching your own:
`tools\sandbox\deploy-to-sandbox.ps1`, then `tools\sandbox\start-sandbox.ps1`. Design notes for the two new screens are
in [docs/lanternfall](docs/lanternfall). The rest of the [guide](docs/guide/README.md) is Selene's and applies as is
(where it says Selene, read Lanternfall).

Internally the code keeps Selene's names (`SeleneCore`, `Selene*` resources): that is on purpose, so Selene's fixes
merge in cleanly. How that works: [docs/lanternfall/UPSTREAM.md](docs/lanternfall/UPSTREAM.md).

## Credits and license

The code is [MIT](LICENSE), like Selene's. Lanternfall is built on **Selene** by Matthew Silva
([github.com/mattxslv/Selene](https://github.com/mattxslv/Selene)), which is built on
[Playnite](https://github.com/JosefNemec/Playnite)'s fullscreen theme template. The Selene name and its crescent crown
logo belong to Selene and are not used here; the lantern mark and the Lanternfall name are this project's own.
Everything else Selene credits (icons, sounds, wallpapers, palettes, avatars) and the libraries the browser uses are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Lanternfall is not affiliated with YouTube, Google,
FreeTube or any console maker.
