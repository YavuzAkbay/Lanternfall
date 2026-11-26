<p align="center"><sub><a href="README.md">Guide</a> · <a href="getting-started.md">Getting started</a> · <a href="spotlight.md">Spotlight</a> · <a href="cards.md">Cards</a> · <a href="store.md">Store</a> · <a href="details-collection.md">Details &amp; Collection</a> · <a href="playing.md">Playing</a> · <a href="browser-youtube.md">Browser &amp; YouTube</a> · <a href="menu.md">Menu</a> · <a href="profiles.md">Profiles</a> · <a href="settings.md">Settings</a> · <a href="themes.md">Themes</a> · <a href="troubleshooting.md">Help</a></sub></p>

# Building from source

No Visual Studio needed: Lanternfall Core builds with the C# compiler that ships with Windows.

```powershell
tools\build.ps1          # builds SeleneCore\bin\SeleneCore.dll (and installs it for the running Playnite)
tools\dev.ps1 new        # restart Playnite on the Lanternfall in this repo (dev.ps1 back = previous theme)
tools\package.ps1        # Lanternfall.pthm + LanternfallCore.pext + LanternfallBrowser.pext in dist\
```

For development, add `SeleneCore\bin` to Playnite's developer add-ons (Settings → For developers) and link `theme\`
into `%APPDATA%\Playnite\Themes\Fullscreen\Lanternfall_1ca6f8dc-492b-44d1-918a-871145f81695`.

- Color themes: `tools/palettes/*.toml` → `python tools/make_palettes.py` → `theme/Palettes`.
- Artwork and media: `tools/make_lanternfall_art.ps1`, `tools/make_lanternfall_media.ps1` (wallpapers, avatars). Sounds:
  `python tools/make_sounds.py`, then `python tools/level_sounds.py`. After changing wallpapers re-run
  `python tools/make_palettes.py` so each palette points at an existing default wallpaper.
- `dotnet build tools/linux-check` checks that Lanternfall Core compiles anywhere (C# 5).

**Checks:** `tools\test.ps1` builds and runs every local suite, with a final pass/fail table.

**Releasing:** follow the [maintainer release checklist](../releasing.md).

`tools\package.ps1` requires a clean committed tree, checks package contents against the fresh stage and rejects
known old-helper content. It writes SHA256SUMS and BUILDINFO.txt with the source commit and tool/input hashes.
The legacy compiler is not deterministic; this records provenance rather than promising reproducible bytes.
For disposable local tests only, `-AllowDirty` marks the source as modified.
Run `pwsh -NoProfile -File tools/tests/security.ps1` on Linux or Windows for the helper retirement and package
regression checks. The Windows-only build, runtime and Defender validation are described in
[Windows migration and validation](../security/windows-validation.md). Version metadata does not replace
Authenticode signing; a publisher certificate is still needed before signed releases can be produced.

## Translating

Lanternfall's words follow Playnite's language when `theme/Localization` has a file for it: `de_DE.txt`, or `de.txt` for
every German. One phrase per line, English on the left, the translation on the right, separated by ` = `. To collect
every English phrase, switch on **Advanced → Collect words to translate**, use Lanternfall for a while, and read
`Lanternfall phrases.txt` on your Desktop. Details in `theme/Localization/README.txt`. Pull requests with translations are welcome.
