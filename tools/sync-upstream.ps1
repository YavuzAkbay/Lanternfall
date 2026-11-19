# Merges Selene's latest changes (upstream/main) into this branch, resolving the conflicts the fork expects on its own:
#   - files Lanternfall removed (Selene's logo art, README pictures, issue bots) stay removed, new ones under those paths too
#   - README.md stays Lanternfall's
#   - theme.yaml / extension.yaml keep Lanternfall's id, name and author but take Selene's new Version
# Anything else that conflicts is listed and the merge is left open for you (fix, `git add`, `git commit`, or `git merge --abort`).
# Then it builds both add-ons and runs Selene's own checks before committing the merge.
#   powershell -File tools\sync-upstream.ps1            fetch, merge, build, test, commit
#   powershell -File tools\sync-upstream.ps1 -NoCommit  same, but leave the merge staged for you to review
#   -CI   (the GitHub workflow) skip the Windows builds: the workflow compiles with dotnet instead
#   -From <ref>  merge another ref instead of upstream/main (for testing this script)
param([switch]$NoCommit, [switch]$CI, [string]$From = 'upstream/main')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
function Invoke-Git { & git @args; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" } }

# paths Lanternfall doesn't carry (the Selene logo and pictures may not be reused; the issue bots are Selene's own)
$removed = @('^docs/readme/', '^docs/screenshots/', '^docs/logo/selene-', '^docs/logo/social-preview', '^tools/art/selene-',
             '^\.github/workflows/(?!build\.yml$|lanternfall-)')
$keepOurs = @('README.md')
$versionFiles = @('theme/theme.yaml', 'SeleneCore/extension.yaml')

if (-not (& git remote | Where-Object { $_ -eq 'upstream' })) { Invoke-Git remote add upstream https://github.com/mattxslv/Selene.git }
if (@(& git status --porcelain).Count -gt 0) { throw 'The working tree has uncommitted changes: commit or stash them first.' }
if ($From -eq 'upstream/main') { Invoke-Git fetch upstream --tags --quiet }
$behind = [int](& git rev-list --count "HEAD..$From")
if ($behind -eq 0) { '"Already up to date with Selene ($From)."'; return }
$theirVersion = ((& git show "${From}:theme/theme.yaml") -match '^Version:' | Select-Object -First 1).Substring(8).Trim()
"Selene has $behind new commit(s), version $theirVersion. Merging..."
& git log --oneline "HEAD..$From" | Select-Object -First 30

& git merge $From --no-ff --no-commit -m "Merge Selene $theirVersion from upstream" 2>&1 | Out-Null
$conflicts = @(& git diff --name-only --diff-filter=U)

foreach ($path in $conflicts) {
    if ($removed | Where-Object { $path -match $_ }) { Invoke-Git rm -q --cached -- $path; Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue; "kept removed: $path" }
    elseif ($keepOurs -contains $path) { Invoke-Git checkout --ours -- $path; Invoke-Git add -- $path; "kept Lanternfall's: $path" }
    elseif ($versionFiles -contains $path) {
        # Selene's file (new version and anything else it changed) with Lanternfall's Id, Name and Author
        $ours = @(& git show "HEAD:$path")
        $lines = @(& git show "${From}:$path") | ForEach-Object {
            $key = if ($_ -match '^(Id|Name|Author):') { $Matches[1] } else { $null }
            if ($key) { $ours | Where-Object { $_ -match "^${key}:" } | Select-Object -First 1 } else { $_ }
        }
        [IO.File]::WriteAllText((Join-Path $repo $path), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
        Invoke-Git add -- $path; "kept Lanternfall's id/name/author, took the rest from Selene: $path"
    }
}
# files Selene added under the removed paths (no conflict, so they came in silently)
foreach ($path in @(& git diff --cached --name-only --diff-filter=A)) {
    if ($removed | Where-Object { $path -match $_ }) { Invoke-Git rm -q -f -- $path; "removed new: $path" }
}

$left = @(& git diff --name-only --diff-filter=U)
if ($left.Count -gt 0) {
    ''
    'These files changed on both sides and need a person (or Claude) to merge them:'
    $left | ForEach-Object { "  $_" }
    ''
    'Open each, keep both changes, remove the <<<<<<< ======= >>>>>>> markers, then: git add <file>; git commit'
    'To give up on this merge: git merge --abort'
    exit 2
}

# Selene's new code may bring back things the fork replaced: point them out
$brand = @(& git grep -n -I -e 'mattxslv' -e 'M 9.109,20.577' -- 'SeleneCore/*.cs' 'SeleneCore/src/*.cs' 'theme/*.xaml' 'theme/Views/*.xaml' |
           Where-Object { $_ -notmatch 'ForkBrand\.cs|Selene.s: Selene_a98e0027' })
if ($brand.Count -gt 0) {
    ''
    'Heads-up: Selene links or the crescent drawing appear in merged code (point them at ForkBrand like the others):'
    $brand | ForEach-Object { "  $_" }
}

if (-not $CI) {
    'Building...'
    & powershell -NoProfile -File (Join-Path $repo 'tools\build.ps1') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'Lanternfall Core does not build after the merge: fix it, then git commit (or git merge --abort).' }
    & powershell -NoProfile -File (Join-Path $repo 'Browser\build.ps1') | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'Lanternfall Browser does not build after the merge.' }
    & powershell -NoProfile -File (Join-Path $repo 'tools\tests\security.ps1') | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "Selene's checks fail after the merge." }
}

if ($NoCommit) { ''; 'Merged and staged, not committed: review with git diff --cached, then git commit.'; return }
Invoke-Git commit -q --no-edit
''
"Done: merged Selene $theirVersion. Push when ready: git push"
