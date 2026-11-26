<p align="center"><sub><a href="README.md">Guide</a> · <a href="getting-started.md">Getting started</a> · <a href="spotlight.md">Spotlight</a> · <a href="cards.md">Cards</a> · <a href="store.md">Store</a> · <a href="details-collection.md">Details &amp; Collection</a> · <a href="playing.md">Playing</a> · <a href="browser-youtube.md">Browser &amp; YouTube</a> · <a href="menu.md">Menu</a> · <a href="profiles.md">Profiles</a> · <a href="settings.md">Settings</a> · <a href="themes.md">Themes</a> · <a href="troubleshooting.md">Help</a></sub></p>

# Browser and YouTube

Two fullscreen overlays, opened from their cards on Spotlight, that work with the controller alone. They come from
the **Lanternfall Browser** add-on (`LanternfallBrowser.pext`), take the colors of your profile's theme, and are
switched on or off under **Settings → Spotlight**. The hints along the bottom always show what each button does right
now.

## Browser

The web page is Microsoft Edge WebView2, which Windows 10 / 11 already include, so sites behave as they do in Edge.

**Getting around.**

| Button | What it does |
|---|---|
| **D-pad** | Jump between links, buttons and fields |
| **A** | Click the selected thing; on a text field, open the on-screen keyboard |
| **B** | Back (a page back, then closes the Browser at the start) |
| **X** | Reload |
| **Y** | Go up to the address bar and its buttons, and back to the page |
| **LB / RB** | Previous / next tab |
| **Left stick** | Scroll the page smoothly |
| **Right stick** | A cursor for anything the D-pad can't reach; **A** clicks it, the D-pad takes you back to link hopping |
| **L3 / R3** | Zoom out / in for this site (remembered per site) |
| **Start** | The Browser menu |

**Typing.** An on-screen keyboard opens for every text field and the address bar, with suggestions and a row of
`.com : / - _ @` for addresses. Typing a plain phrase searches the web. Prefer your phone's keyboard? See
[your phone as a remote](#your-phone-as-a-remote).

**The Browser menu (Start)** has: new tab, all tabs, close tab, add or remove a bookmark, find in page, page tools,
history, downloads, phone link, settings and close. *Page tools* has reader mode (a clean, big-text version of an
article), the phone or desktop version of a site, and zoom.

- **Tabs** are shown as cards in a switcher (X closes one, Y opens a new one).
- **Find in page:** A goes to the next match, Y to the previous one.
- **Downloads** are off until you allow them, and a file is only saved to your Downloads folder after you say yes on
  the screen.
- **Video sites** play in real fullscreen.
- **An ad blocker** is built in and can be switched off in the Browser's settings.
- **Page size.** On a 4K TV pages are scaled up so they read like a 1080p one from the sofa. If it is still too small,
  set **Page size** to "Auto larger", or pick a fixed percentage.
- **Auto-hide bars** hides the address bar and hints while you scroll; Y brings them back.

Links opened from elsewhere in Lanternfall (the news, for instance) open here, in a tab of their own.

## YouTube

A TV-style YouTube that needs no account. Along the top are **Home, Subscriptions, Search, History, Playlists and
Channels**; LB / RB move between them, the D-pad moves through the videos, **A** plays, **Y** opens a video's options
and **X** refreshes.

- **First run** asks what you are into and suggests news and channels to start with, so Home isn't empty. You can
  also bring your own subscriptions: with [FreeTube](https://freetubeapp.io) installed, Lanternfall reads its
  subscriptions, history, playlists and settings (it never changes FreeTube's files).
- **Search** suggests as you type and keeps your recent searches.
- **Channels** have tabs for videos, Shorts, live streams and playlists, and you can subscribe or unsubscribe from the
  player.
- **Subscriptions, history and watch positions** are kept on your PC, per profile. A video resumes where you left it.

**The player.** Quality up to 4K at 60 fps (AV1 where your graphics card decodes it), with
speed, captions and audio tracks under the controls.

| Button | What it does |
|---|---|
| **A** | Play / pause |
| **Left / Right** | Seek 10 s (hold to go faster); on a livestream, rewind / forward |
| **LT / RT** | Jump 60 s |
| **Down** | The controls: quality, speed, captions, audio track, related, channel |
| **X** | Captions on / off |
| **Y** | Up next (on a livestream: "Go live" when you're behind) |
| **LB / RB** | Previous / next video |
| **B** | Back to the list |

The controls fade after a few seconds; any press brings them back. With [SponsorBlock](https://sponsor.ajay.app) on in
FreeTube, sponsor segments are skipped.

**If a video won't play.** YouTube changes its site from time to time. Check **Menu → Update** for a newer Lanternfall
Browser, and if there is none, report it from the menu (**Report a problem**). See also [Help](troubleshooting.md).

## Your phone as a remote

One link covers everything: open **Start → Phone link** and scan the QR
code with your phone's camera, once. The page that opens is a:

- **Keyboard**, for any text field in the Browser or YouTube (also reachable from the on-screen keyboard's *Back*
  button). Passwords stay on the pad's keyboard unless you turn on *Phone keyboard for passwords*.
- **Trackpad** for the Browser.
- **YouTube remote**: search, play, pause and skip.
- **Send a link**: paste or share a link on the phone and it opens on the TV.

It works over your home network. The first time, Windows may ask to allow Lanternfall through its firewall; turn
on **Let phones reach this PC** in the Browser's settings if it doesn't connect. **Reset phone link** makes a new
address and cuts off phones that had the old one, so use it if your phone is lost or you shared the code.

## Resting the controller while you watch

Under **Controller rest while watching** in Lanternfall's settings: after a minute without input Lanternfall stops
polling the controller (**Low power**), or lets you switch the pad itself off by holding its home button (**Power
off**, enabled after a one-time check). Playback is never touched.

## About YouTube

Lanternfall is not affiliated with YouTube or Google. Using YouTube through a third-party app can go against its Terms
of Service, and what is allowed depends on where you live: you use it at your own risk. Lanternfall does not host,
download or re-distribute videos. Turn the YouTube card off under **Settings → Spotlight** if you'd rather not have it.
