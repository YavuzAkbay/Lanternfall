# Maintainer release checklist

Lanternfall releases are made by hand from a clean, tested tree. Never publish a release or change `main` without the maintainer's say-so.

## Prepare

1. Bump `Version:` in `theme/theme.yaml` and `SeleneCore/extension.yaml` (both 1.5.x, +1 per release). `Browser/extension.yaml` has its own version and must go up whenever the Browser changed, or nobody is offered it. A Browser-only fix is a suffix release (`1.5.13-1`).
2. Move the `CHANGELOG.md` "Unreleased" entries under `## <ver> - <date> (Lanternfall Core <ver>; Lanternfall Browser <ver>)`.
3. Commit as one change ("Lanternfall <ver> (Browser <x>): ...").
4. Run `tools\test.ps1`. The `library` suite needs a Playnite folder with `Playnite.SDK.dll`; it looks in `%USERPROFILE%\selene-fresh\Playnite`, the installed Playnite, then `..\Sandbox\Playnite`.

## Package

1. The git-ignored YouTube web route overlay (`Browser/ytweb`, `Browser/lib/ytweb`) must not ship. Move both out of the tree (to a durable folder), delete `Browser\build`, and `tools\package.ps1` refuses to continue if `Browser\build\ytweb` exists.
2. With a clean tree run `tools\package.ps1` (it writes `dist\` including `SHA256SUMS` and `BUILDINFO.txt` with the commit and the Browser version, which the in-app update check reads).
3. Check that `LanternfallBrowser.pext` holds only `SeleneBrowser.dll`, the WebView2 DLLs, the icon and the yaml. Move the overlay back after publishing.

## Publish (only when the maintainer says so)

1. Tag `v<ver>` and push `main` and the tag.
2. `gh release create v<ver> --repo YavuzAkbay/Lanternfall --target <sha> --title "Lanternfall <ver>" --notes-file <notes> dist\Lanternfall.pthm dist\LanternfallCore.pext dist\LanternfallBrowser.pext dist\SHA256SUMS dist\BUILDINFO.txt`. Always pass `--repo`.
3. Download the assets into an empty folder and check them against `SHA256SUMS`.

Release notes are short and plain, like one developer talking, with an Install / update section. Show the exact text to the maintainer before posting.
