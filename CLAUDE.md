# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Lanternfall is a Playnite fullscreen theme with a controller-only Browser and YouTube add-on. It started from [Selene](https://github.com/mattxslv/Selene) (MIT) and is now an independent project: its own repo (not a GitHub fork), no upstream remote, no automatic merging from Selene. The only remote is `origin` = Lanternfall. It is a Windows-only, WPF, .NET Framework 4.x project with **no dotnet SDK / MSBuild**: everything compiles with the `csc.exe` that ships with Windows, as **C# 5** (no `$""` interpolation, `?.`, `nameof`, `=>` members, `out var`, tuples, pattern `is T x`, local functions). CI (`.github/workflows/build.yml`) compiles SeleneCore on Linux through `tools/linux-check` with the same restrictions.

The parent folder (`..\`, "Nomad") is not a repo. It holds the portable sandbox Playnite (`..\Sandbox\Playnite`) and backups (`..\Backups`, `..\backup`). The old pre-git copies (`..\src`, `..\scripts`) were deleted on 2026-11-26; `docs/lanternfall/SPEC.md` and `DEVLOG.md` still mention those old `src\...` paths.

## Three deliverables

| Part | Source | Build | Output |
| --- | --- | --- | --- |
| Theme (XAML) | `theme/` | none (copied/linked) | `dist\Lanternfall*.pthm` |
| Lanternfall Core plugin | `SeleneCore/` (`SeleneCore.cs`, `src/*.cs`, `Localization/`) | `tools\build.ps1` | `SeleneCore\bin\`, staged in `SeleneCore\obj\stage` |
| Lanternfall Browser plugin | `Browser/` | `Browser\build.ps1` | `Browser\build\` |

`tools\package.ps1` builds Core and packs all three with Playnite's `Toolbox.exe` into `dist\` (needs a clean git tree unless `-AllowDirty`). Needs an installed Playnite for `Playnite.SDK.dll` (`%LOCALAPPDATA%\Playnite`; Browser falls back to the sandbox copy).

`tools\build.ps1` compiles every `*.cs` in `SeleneCore\` and `SeleneCore\src\` automatically; `Browser\build.ps1` compiles every `*.cs` in `Browser\`. The test scripts under `tools\tests\` instead list their `.cs` inputs by hand (see below).

## Commands

```powershell
tools\build.ps1                       # Core
Browser\build.ps1                     # Browser add-on
tools\test.ps1 [-SkipBuild]           # build + every suite + node test; nonzero exit on any failure
tools\tests\bugs.ps1                  # one suite (also: check, interaction, library, security, backgroundchanger)
node tools/tests/trailer-loop.cjs                               # trailer canvas pause/resume test
tools\update-youtubejs.ps1 [-Version x.y.z] [-TestOnly]          # update the bundled YouTube.js; real-playback check, rolls back on failure (network)
```

Suites are `tools\tests\<name>.ps1` + `<Name>.Check.cs`: each csc-compiles a small console exe and exits nonzero on failure (`bugs`/`interaction`/`library`/`backgroundchanger` link the built `SeleneCore.dll`, so build first; `check` and `security` compile selected source files directly). There is no per-test filter; to run one case, edit/narrow the `.Check.cs` or call the suite exe. A new `src/*.cs` file used by `check.ps1` must be added to its hand-written source list.

**Real-app testing** runs only in the isolated sandbox Playnite, never the installed one:

```powershell
tools\sandbox\deploy-to-sandbox.ps1                 # theme + both plugins -> ..\Sandbox\Playnite
tools\sandbox\start-sandbox.ps1
tools\sandbox\test-overlays.ps1 -Open youtube|browser|none -Keys "9000 pad:A 800 shot:name" -Seconds 40
```

`test-overlays.ps1` builds, deploys, injects scripted pad presses (via Selene's `%TEMP%\selene-dev-keys` hook), saves `%TEMP%\selene-shot-<name>.png`, and refuses to run while *any* Playnite is open. The sandbox starts on the "Who's playing?" profile screen, so keys must begin `9000 pad:A`. `shot:` uses RenderTargetBitmap and cannot see WebView2 video — screenshot the desktop instead. `tools\dev.ps1 new|back` instead restarts the *real* Playnite on this theme (Playnite allows one instance per session).

## Architecture

**Core ↔ theme ↔ Browser wiring.** The theme (`theme/Views/*.xaml`, `App.xaml`) declares the UI; `SeleneCore.dll` is a Playnite `GenericPlugin` that drives it (`HomeNav.cs` is the large home-screen/input hub; `Ui.cs` has the shared pill/tile/prompt look; `Lang.cs` routes all on-screen strings through localization). Browser and YouTube live in the **separate** `SeleneBrowser.dll` plugin (`SeleneBrowserPlugin` in `Browser/SeleneBrowser.cs`), which cannot reference SeleneCore types. `Main.xaml` hosts two empty `ContentControl`s, `SeleneBrowser_BrowserSurface` and `SeleneBrowser_YouTubeSurface`; the Browser plugin fills them, and `HomeNav` shows/hides the overlays by those names.

**Pad contract** (`BrowserControl` and `YouTubeControl` both implement it, called by `HomeNav`): `bool HandlePad(string button)` (false = at top level, close the overlay), `OnShown()`, `OnHidden()`, `string Hints` (INotifyPropertyChanged). Button names: `A B X Y Start Back LeftShoulder RightShoulder TriggerLeft TriggerRight DPadUp/Down/Left/Right LeftStick RightStick`; left-stick directions arrive as DPad*, right-stick directions do **not** (the add-on polls XInput itself via `PadInput.cs`, only while shown and active). Full spec in `docs/lanternfall/SPEC.md`.

**Pad input reliability.** Playnite polls the controller every 16 ms, so very fast taps are lost; `SeleneCore/src/PadTapGuard.cs` (option `tapguard`) polls XInput at 2 ms and replays missed presses through Playnite's private `ProcessButtonState`. `HomeNav.OnPreProcessInput` reports real arrivals to it.

**Browser add-on specifics.** The web page is Edge WebView2 (`lib/webview2` DLLs are committed; the x86 loader is shipped beside them because Playnite is 32-bit). The YouTube player is a windowed WebView2 with its controls in `PlayerOverlayWindow` (composition hosting capped video frame rate). UI should use `Browser/Theme.cs` helpers (`Theme.Tile/Card/Glass`, `HintBar`) that mirror SeleneCore's `Ui`, and read palette brushes at runtime with `TryFindResource("SeleneInkBrush"/"SeleneAccentBrush"/…)` with fallbacks. Writable data goes in the plugin user-data dir (`SeleneBrowserPlugin.DataDir`), never next to the DLL. Never block the UI thread on network I/O (`Task.Run` + `Dispatcher.BeginInvoke`).

## Code conventions inherited from Selene

The code still uses Selene's internal names, so keep them (a mass rename would only churn the history):

- **Do not rename Selene identifiers** (namespaces, `SeleneCore`, `Selene*` resource keys, file names). Branding is applied through `ForkBrand.cs` (name, lantern mark, `Repo` slug) hooked into `Lang.Apply/T`; links to GitHub should use `ForkBrand.Repo`, and the crescent logo must not be drawn (Selene's name/logo are not licensed to Lanternfall).
- Tags are `v<version>`. Core is 1.5.x, Browser has its own version (`Browser/extension.yaml`). Never publish a release or push to `main` without the maintainer's say-so.
- Never run `git stash` here — uncommitted Browser work often lives in the tree. `.gitignore` excludes `dist/`, `bin/`, `obj/`, `Browser/build/`, `.claude/` and `CLAUDE.local.md` (this `CLAUDE.md` is tracked in a public repo).

## When the maintainer says "check for updates" (or similar)

Check every outside source Lanternfall depends on, report what is new, and change nothing that would ship without asking:

1. **YouTube playback**: the optional YouTube web route (`Browser/ytweb`, `Browser/lib/ytweb`, `tools/update-youtubejs.ps1`, `tools/youtubejs`) is a **local-only overlay**: it is git-ignored and must never be committed or published (`Browser\build.ps1` copies it only when present, and `YouTubeWeb.Available` is false without it). With the overlay present, run `tools\update-youtubejs.ps1` and report the outcome; without it, check that playback still works through the other sources. Fixes to the overlay stay local; fixes elsewhere ship with a raised `Version` in `Browser/extension.yaml` and a release.

Never commit, push or release as part of this; summarize and let the maintainer decide.

**Shipping a Browser-only fix** (a new YouTube.js, a YouTube change): raise `Version` in `Browser/extension.yaml` (it must go up, or nobody is offered it), keep the theme/Core version, and release it as a suffix release (`1.5.8-1`, `-2`, ...). The in-app update check (`SeleneCore/src/ForkUpdate.cs`, hooked into Selene's `UpdateCheck`/`UpdateScreen`/`MenuScreen`) offers a release whose Browser is newer than the installed one even when the theme version is unchanged, and then installs only `LanternfallBrowser.pext`. It reads the release's Browser version from the `Lanternfall Browser: x.y.z` line that `tools\package.ps1` writes into `BUILDINFO.txt`, falling back to "Lanternfall Browser x.y.z" in the release notes, so keep naming it there too. A release with a newer theme version is offered as the full update (all three files), as in Selene.

## Environment gotchas

- Inspecting Playnite's assemblies (`Playnite.dll`, `Playnite.FullscreenApp.exe`) from PowerShell needs the 32-bit host: `C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`.
- WPF: a huge `CornerRadius` (e.g. 999) draws an ellipse, not a capsule — pills set radius = height/2 on `SizeChanged`.
- A `CompositionTarget.Rendering` handler makes WPF tick at the *fastest* monitor's refresh rate; `Ui.OnFastestScreen()` gates `Smoothness.Listen` for that reason.
- On-screen text flows through `Lang` and `SeleneCore/Localization/en_US.xaml`; the `bugs` suite requires each setting's caption (`Features.All[].About`) to stay ≤ 140 characters.
