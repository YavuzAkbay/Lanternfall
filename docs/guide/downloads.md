# Optional media downloads

Lanternfall does not start optional game-media downloads simply because you installed it. New installations and
upgrades start with trailers, screenshots, logos/backgrounds, missing metadata and game-song setup off.
Previously downloaded files are kept. They can still be displayed or played while downloads are off.

When your library has games but no Lanternfall art yet, Lanternfall asks one question once, "Get art for your games?". Yes is the
consent for art, screenshots, trailers for the games you select and missing details together (it lists the sources and the save
folders); songs are separate. Not now leaves everything off and puts a red dot on Lanternfall settings; Games > Get art for your games asks again, and the dot goes once the art is allowed. The same choices stay in Lanternfall settings.

Choose what you want in Lanternfall settings:

- **Spotlight > Download trailers:** Off, Games you select, or Whole library. Changing the enabled scope asks
  for confirmation again. Selected-game mode does not queue neighbouring games.
- **Spotlight > Download screenshots:** only fetch when you select a game; no whole-library startup pass.
- **Spotlight > Download logos and backgrounds:** selected-game art and appearance-preview samples.
- **Games > Complete missing game details:** optional library metadata lookup, including missing covers and
  backgrounds, using the configured metadata add-ons and Steam.

Before any of these categories is enabled, its confirmation shows the scope and actual save directories,
including the configured Lanternfall media folder or Playnite library location. A switch or old inherited default
without that confirmation cannot authorize a download. Playback switches do not grant download permission.
“Not now” leaves the category off. The native add-on settings page directs download choices to Lanternfall's
fullscreen settings so it cannot skip the confirmation.

Turning a category off prevents further requests. A request already transferring can finish. UniPlaySong
owns its running download queue; use its own controls to stop that queue. For tools Lanternfall configured,
automatic library-update/game-install music downloads are disabled; manually configured independent add-ons
are not reconfigured. Playnite, Extra Metadata Loader, UniPlaySong and other add-ons may have independent
automatic-download settings. These controls are for Lanternfall's optional game-media work, not a global network block.

Main destinations, also shown before enabling:

- Trailers/logos: Playnite's `ExtraMetadata/games/<game ID>`.
- Screenshots: `Screenshots-v2/<game ID>` under the configured Lanternfall media folder.
- Backgrounds/preview samples: `Hero` and `Sample` under that media folder.
- Songs: Playnite's `ExtraMetadata/UniPlaySong/Games/<game ID>`.
- Song tools: `Tools` under Lanternfall Core's user-data directory.
- Metadata covers/backgrounds: the files directory of the configured Playnite library.

No download is silently opted back in after an upgrade or reset. No existing media files are deleted as
part of the consent change. Old screenshot caches with unverified media matches may be left unused rather
than displayed under the wrong game; their files remain on disk.
