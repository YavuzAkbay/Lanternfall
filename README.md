<p align="center">
  <img src="docs/logo/lanternfall-banner.png" alt="Lanternfall" width="100%">
</p>

<p align="center">
  <b>A calm, console-style fullscreen theme for <a href="https://playnite.link">Playnite</a>: your games, the web and YouTube, all built for the couch.</b><br>
  Everything works with a controller: no keyboard, no mouse.
</p>

<p align="center">
  <a href="#install">Install</a> ·
  <a href="#features">Features</a> ·
  <a href="docs/guide/README.md">Guide</a> ·
  <a href="#building-from-source">Build</a> ·
  <a href="#credits-and-license">Credits</a>
</p>

## Features

Lanternfall is a living-room theme: sit back, pick up the pad, and go from your games to the web to a film without
ever reaching for a keyboard or mouse.

### Made for the couch

- **Controller first, everywhere.** Every screen, the web browser and YouTube included, is built for a gamepad and
  readable from across the room: big tiles, clear button hints, no tiny click targets.
- **Spotlight.** One row for your cards and your games, with art, trailers, play time and achievements at a glance.
- **Games feel like a console.** A launch experience that keeps the game's art on screen until the game is really
  there, plus captures, achievements, activity, a store and per-person profiles ("Who's playing?").
- **23 color themes**, each with its own wallpaper, dark and light. Pick one per profile; every screen follows it,
  the Browser and YouTube too.

### More than games: watch and browse from the sofa

- **Browser.** A full web browser in a fullscreen overlay, driven entirely by the pad: the D-pad moves between links
  and buttons, the left stick scrolls smoothly, the right stick drives a cursor, and an on-screen keyboard appears for
  every text field and the address bar (or use your phone: one QR code, scanned once, gives you a keyboard, a trackpad and a way to send links). Tabs shown as cards, find in page,
  reader mode, bookmarks, history, downloads, zoom per site, and video sites play in real fullscreen (Microsoft Edge
  WebView2). On a 4K TV pages are scaled up to be readable from the sofa (adjustable under **Page size**).
- **YouTube.** A TV-style YouTube screen with no account: subscriptions, search with suggestions, history,
  playlists, channels and livestreams, and a player with quality up to 4K at 60 fps (AV1 where your graphics card
  decodes it), speed, captions and audio tracks. It can import your
  [FreeTube](https://freetubeapp.io) subscriptions, history and settings (read-only) and skips sponsor segments with
  [SponsorBlock](https://sponsor.ajay.app) when FreeTube has it on.
- **Controller rest while you watch.** Settle in for a film without the pad draining or nudging anything: after a
  minute without input Lanternfall stops polling the controller (**Low power**), or lets you switch the pad itself off
  by holding its home button (**Power off**, enabled after a one-time check). Playback is never touched. Set it under
  **Controller rest while watching** in the settings.

The Browser and YouTube are Spotlight cards: switch them off in **Settings → Spotlight** if you don't want them.

## Install

1. Download `Lanternfall.pthm`, `LanternfallCore.pext` and `LanternfallBrowser.pext` from the
   [latest release](../../releases/latest).
2. Open each file (or drag it onto Playnite) and confirm. Restart Playnite.
3. Fullscreen mode → **Menu → Playnite settings → Appearance → Theme → Lanternfall**.

Playnite 10 or newer, Windows 10 / 11, a controller recommended. The browser uses Microsoft Edge WebView2, which
Windows 10 / 11 already include.

**Updating.** Lanternfall offers new versions from its menu (**Menu → Update**, with a notification once a day): the
theme, Lanternfall Core and Lanternfall Browser together when they move to a new Selene version, and the Browser alone
when a release only brings a newer Lanternfall Browser. Other releases tagged with a suffix (like `1.5.7-3`) are
installed by hand: download the files the release notes name and open them as in step 2.

**If YouTube stops playing.** YouTube changes its site from time to time, and apps other than its own run into it. When that
happens a fix may come in a new Lanternfall Browser release (**Menu → Update**, or look in the [releases](../../releases)).
Nothing newer yet? Report it (**Menu → Report a problem**).

**About YouTube.** Lanternfall is not affiliated with YouTube or Google. Using YouTube through a third-party app can go
against YouTube's Terms of Service, and what is allowed depends on where you live: you use that part at your own risk and
are responsible for following the rules that apply to you. Lanternfall does not host, download or re-distribute videos.
Turn the YouTube card off under **Settings → Spotlight** if you don't want it.

## Credits and license

The code is [MIT](LICENSE). Lanternfall started from [Selene](https://github.com/mattxslv/Selene) by Matthew Silva (MIT) and is now its own project,
built on [Playnite](https://github.com/JosefNemec/Playnite)'s fullscreen theme template. The Lanternfall name and lantern
mark are this project's own. Icons, sounds, wallpapers, palettes, avatars and the libraries the browser uses are
credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Lanternfall is not affiliated with YouTube, Google,
FreeTube or any console maker.
