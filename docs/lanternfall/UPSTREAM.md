# Staying in sync with Selene

Lanternfall is a git fork of [Selene](https://github.com/mattxslv/Selene). Its history starts from Selene's own commits,
so git knows exactly which of Selene's changes Lanternfall already has, and bringing in Selene's bug fixes is a normal
merge.

## How it stays easy to merge

The fork changes as few of Selene's lines as it can:

| What | Where | Why it rarely conflicts |
| --- | --- | --- |
| The name shown on screen | `SeleneCore/src/ForkBrand.cs` (new file) + 3 lines in `Lang.cs` | Every text Selene shows already passes through `Lang` (for translations); "Selene" becomes "Lanternfall" there, so Selene's hundreds of strings stay untouched. |
| The lantern mark | `ForkBrand.cs` + a few lines in `Ui.cs`, `IntroLogo.cs`, `PlatformIcons.cs` | Only the drawing lines are swapped. |
| Add-on ids, names | `theme/theme.yaml`, `SeleneCore/extension.yaml`, two lines in `SeleneCore.cs` | `tools/sync-upstream.ps1` resolves the version bumps in the yaml files by itself. |
| Update / report links | one line each in `UpdateCheck.cs`, `UpdateScreen.cs`, `ReportScreen.cs`, `StoreData.cs`, `StoreImages.cs` | They point at `ForkBrand.Repo`. |
| Browser and YouTube | `Browser/` (its own add-on), `HomeNav.cs`, `Main.xaml`, `App.xaml` | `Browser/` is new; the hooks are added blocks, not edits of Selene's lines. |
| Removed | Selene's logo art, README pictures, issue bots | `tools/sync-upstream.ps1` keeps them removed. |

Internal names (`SeleneCore`, the `Selene*` resource keys, namespaces, file names) stay Selene's on purpose. Renaming
them would make every one of Selene's changes conflict.

## Getting Selene's updates

**Automatic.** The `Selene updates` workflow (`.github/workflows/lanternfall-upstream-sync.yml`) checks Selene once a
day. If the changes merge cleanly and compile, it opens a pull request called "Merge Selene x.y.z": try it, then merge
it. If they conflict, it opens an issue telling you to merge by hand.

**By hand** (or when the workflow says so), on your PC in the repo folder:

```powershell
git pull
powershell -File tools\sync-upstream.ps1
git push
```

The script fetches Selene, merges, settles the known conflicts, then builds both add-ons and runs Selene's own checks
before committing. If a file really changed on both sides, it stops and lists it: open it, keep both changes (remove the
`<<<<<<<` `=======` `>>>>>>>` markers), `git add` it and `git commit`. `git merge --abort` gives up and puts everything
back. Claude Code can do this part for you: ask it to "finish the Selene merge".

After merging, it also warns if Selene's new code links to Selene's GitHub or draws the crescent somewhere new: point
those at `ForkBrand` like the others.

## Releasing

Keep Lanternfall's version the same as the Selene version it is based on (the script takes Selene's version on every
merge), and tag releases `v<version>`, for example `v1.5.7`. The update check in Lanternfall Core looks at this repo's
latest release and downloads `Lanternfall.pthm`, `LanternfallCore.pext` and `LanternfallBrowser.pext` from it.
