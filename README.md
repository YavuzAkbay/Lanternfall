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

## What is Lanternfall?

Lanternfall turns Playnite's fullscreen mode into a living-room experience. Sit back, pick up the pad, and move from
your library to a web page to a film without ever reaching for a keyboard or mouse. It is made of three parts that
install together: the **theme** (the look and every screen), **Lanternfall Core** (the plugin that makes the theme
work) and **Lanternfall Browser** (the web browser and YouTube).

## Features

### Your games, the console way

- **Spotlight.** One row for your cards and your games. The selected game's art fills the screen, with its logo,
  play time, last played and platform, and a trailer that starts playing when you rest on it (its own song with it).
  Filter tabs, drag-to-reorder and a quick "Find a game" are a button away.
- **Cards at the start of the row.** *Captures* (screenshots and clips from Steam, Xbox Game Bar, NVIDIA and your own
  folders, played right in the viewer), *Activity* (your week, your most played, games worth picking up again),
  *Surprise me* (a slot-machine pick for tonight) and a *Store* with free games, sales and new releases.
- **A launch that feels like a console.** The game's art stays on screen until the game is really showing and making
  sound, then fades into it. Guide takes you back home while it runs (offline games pause), Close Game ends it
  cleanly, and saves are backed up quietly afterwards when Ludusavi is installed.
- **Game pages and a full Collection.** Details with scores, genres, screenshots, your captures and activity, and
  "more like this" from your own library, plus Playnite's complete grid in the same look.
- **Profiles.** A "Who's playing?" screen where everyone gets their own name, avatar, theme, favorites, recently
  played and play time.
- **23 color themes**, dark and light, each with its own wallpaper. Pick one per profile and every screen follows it,
  the Browser and YouTube too. Bring your own wallpapers and per-game art if you like.
- **Playnite's own settings, dialogs and menus** share the same look, so nothing jumps out of the theme.

### More than games: browse and watch from the sofa

- **Browser.** A full web browser in a fullscreen overlay, driven by the pad: the D-pad hops between links and
  buttons, the left stick scrolls smoothly, the right stick moves a cursor, and an on-screen keyboard appears for
  every text field and the address bar. Tabs are cards, with find in page, reader mode, bookmarks, history, downloads,
  zoom per site and a built-in ad blocker. Video sites play in real fullscreen (Microsoft Edge WebView2), and on a
  4K TV pages are scaled up to be readable from across the room (adjustable under **Page size**).
- **Your phone as a keyboard.** One QR code, scanned once, gives you a keyboard, a trackpad, a YouTube remote and a
  way to send a link straight to the TV.
- **YouTube.** A TV-style YouTube screen with no account: subscriptions, search with suggestions, history, playlists,
  channels and livestreams, and a player with quality up to 4K at 60 fps (AV1 where your graphics card decodes it),
  speed, captions and audio tracks. It can import your [FreeTube](https://freetubeapp.io) subscriptions, history and
  settings (read-only) and skips sponsor segments with [SponsorBlock](https://sponsor.ajay.app) when FreeTube has it
  on. On first run it asks what you like and suggests news and channels to start with.
- **Controller rest while you watch.** Settle in for a film without the pad draining or nudging anything: after a
  minute without input Lanternfall stops polling the controller (**Low power**), or lets you switch the pad off by
  holding its home button (**Power off**, enabled after a one-time check). Playback is never touched. Find it under
  **Controller rest while watching** in the settings.

The Browser and YouTube are Spotlight cards: switch them off in **Settings → Spotlight** if you don't want them.

### Yours to tune

Lanternfall has a settings page for almost everything: which cards show, how long before trailers start, filter tabs,
what the Store opens on, music and loading-screen sound, button prompts (Xbox or PlayStation), close to tray, and more.
The [guide](docs/guide/README.md) walks through every screen.

## Install

1. Download `Lanternfall.pthm`, `LanternfallCore.pext` and `LanternfallBrowser.pext` from the
   [latest release](../../releases/latest).
2. Open each file (or drag it onto Playnite) and confirm. Restart Playnite.
3. Fullscreen mode → **Menu → Playnite settings → Appearance → Theme → Lanternfall**.

Playnite 10 or newer, Windows 10 / 11, a controller recommended. The browser uses Microsoft Edge WebView2, which
Windows 10 / 11 already include.

**Updating.** Lanternfall offers new versions from its own menu (**Menu → Update**, with a notification once a day).
A full update installs the theme, Lanternfall Core and Lanternfall Browser together; when a release only brings a newer
Lanternfall Browser (a YouTube fix, say), just that part is installed. Releases tagged with a suffix (like `1.5.13-1`)
are installed by hand: download the files the release notes name and open them as in step 2.

**If YouTube stops playing.** YouTube changes its site from time to time, and apps other than its own run into it. When
that happens a fix may come in a new Lanternfall Browser release (**Menu → Update**, or look in the
[releases](../../releases)). Nothing newer yet? Report it (**Menu → Report a problem**).

**About YouTube.** Lanternfall is not affiliated with YouTube or Google. Using YouTube through a third-party app can go
against YouTube's Terms of Service, and what is allowed depends on where you live: you use that part at your own risk
and are responsible for following the rules that apply to you. Lanternfall does not host, download or re-distribute
videos. Turn the YouTube card off under **Settings → Spotlight** if you don't want it.

## Building from source

Lanternfall builds on Windows with the C# compiler that ships with it: no SDK or Visual Studio needed. The
[building guide](docs/guide/building.md) covers the build scripts, the sandbox Playnite used for testing and how
releases are made.

## Credits and license

The code is [MIT](LICENSE). Lanternfall is an independent project. It began from the foundation of
[Selene](https://github.com/mattxslv/Selene) by Matthew Silva (MIT), and our thanks go to him for that start; it is
built on [Playnite](https://github.com/JosefNemec/Playnite)'s fullscreen theme template, and everything since, the
look, the name, the lantern mark, the Browser and YouTube, is Lanternfall's own. Icons, sounds, wallpapers, palettes,
avatars and the libraries the browser uses are credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Lanternfall is not affiliated with YouTube, Google, FreeTube, Selene or any console maker.
