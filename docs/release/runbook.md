# First public release runbook

**Nothing in this file has been executed.** It is written for review first and
running second. Every step is a PowerShell block you can read before you trust
it. Run them in order, from the shell's stated location, and stop at the first
step whose check fails.

The one irreversible moment is step 7. Objects pushed to a public GitHub
repository are recoverable by anyone who knows the SHA even after a
force-push, a branch delete, or a repo rename. That is why the public remote
receives exactly one orphan commit and never the old refs.

Set these once at the top of the session:

```powershell
$ErrorActionPreference = 'Stop'
$Src      = 'C:\Users\Midona\OneDrive\Dokument\GitHub\Poser'   # the checkout the release is cut from
$Fresh    = 'C:\tmp\Poser-public-root'                          # the new orphan repo (must not exist yet)
$Scratch  = 'C:\tmp\Poser-release-verify'                       # throwaway verification clone
$Backup   = "C:\tmp\poser-backup-$(Get-Date -Format yyyyMMdd-HHmm).bundle"
$Tag      = 'v0.9.0-beta.1'                                     # PROPOSED — see step 0
```

---

## Step 0 — Settle the version and the placeholders

The git tag carries the prerelease word; the plugin manifest cannot. A Dalamud
`AssemblyVersion` is four numeric parts, so `0.9.0-beta.1` is not expressible
there. The proposal, already written into the tree and flagged as a proposal:

| Where | Value |
|---|---|
| Git tag | `v0.9.0-beta.1` |
| `Poser/Poser.json` → `AssemblyVersion` | `0.9.0.0` |
| `Poser/Poser.csproj` → `<Version>` | `0.9.0.0` |
| Manifest punchline | ends with "Beta." so the listing says it out loud |

If you want a different number, change all three together — the manifest and
the assembly must match or Dalamud rejects the plugin.

```powershell
# The manifest and the csproj must agree. Prints both; they must be identical.
(Get-Content "$Src\Poser\Poser.json" -Raw | ConvertFrom-Json).AssemblyVersion
([xml](Get-Content "$Src\Poser\Poser.csproj")).Project.PropertyGroup.Version | Where-Object { $_ }

# Hard gate: no placeholder may survive into a public repo.
$placeholders = Select-String -Path "$Src\Poser\Poser.json" -Pattern 'REPLACE-WITH-OWNER'
if ($placeholders) {
    $placeholders
    throw 'RepoUrl/IconUrl still hold placeholders. Set the real URL, or delete IconUrl if there is no icon yet.'
}
```

## Step 1 — Land every lane, then gate

Lane branches merge into the integration head first. The gate below is the
authoritative one; it is the same pair of commands every lane ran.

`docs/release/CHANGELOG.md` is written for the **fully converged** head — it
credits reference pictures and the on-screen text nodes, which arrive on their
own lanes. If you cut the release before every lane lands, re-read the changelog
against what is actually in the tree and delete what is not there. Over-claiming
in a first beta is the one release defect users notice immediately.

```powershell
Set-Location $Src
git status --short          # must print nothing
git log --oneline -1        # record this SHA in the release notes

dotnet build Poser.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Release build failed. Stop.' }

dotnet test Poser.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Stop.' }
```

Expect 0 warnings, 0 errors, and the full suite green. A Debug build
auto-deploys to the live game — do not run one here.

## Step 2 — Tag the pre-release

An annotated tag, on the gated commit, in the *old* repository. This tag is the
thing step 6 verifies the fresh root against. It is never pushed to the public
remote.

```powershell
Set-Location $Src
git tag -a $Tag -m "Poser $Tag — first public beta"
git tag --verify $Tag 2>$null; git show --stat --no-patch $Tag
```

## Step 3 — Bundle every ref to a dated backup, outside the repo

This is the undo button for everything that follows. `--all` plus explicit tags
captures every branch, tag and note; the bundle is a single file you can clone
from later. It goes to `C:\tmp`, never inside `$Src`, so no later copy step can
sweep it into the public tree.

```powershell
Set-Location $Src
git bundle create $Backup --all --tags
git bundle verify $Backup            # must report the bundle is okay
"{0:N1} MB" -f ((Get-Item $Backup).Length / 1MB)

# Prove it restores before you rely on it.
git clone --mirror $Backup "$Backup.checkclone"
git -C "$Backup.checkclone" for-each-ref --count=20 --format='%(refname)'
Remove-Item "$Backup.checkclone" -Recurse -Force
```

Copy `$Backup` somewhere off this machine before step 7.

## Step 4 — Build the fresh orphan root

A brand-new repository with one commit and no ancestry. Built by copying the
working tree with the exclusion list from
[exclusions.md](exclusions.md) applied, so nothing can arrive through a stale
index.

```powershell
if (Test-Path $Fresh) { throw "$Fresh already exists. Move it aside; do not merge into it." }
New-Item -ItemType Directory $Fresh | Out-Null

# /MIR mirrors; /XD excludes directories; /XF excludes files. Directory names
# are matched anywhere in the tree, which is what we want for bin/obj.
$excludeDirs = @(
    "$Src\Brio", "$Src\Ktisis", "$Src\Anamnesis",
    "$Src\DevHost", "$Src\Norvrandt.Tests",
    "$Src\PosingCore\Data\GameData",
    "$Src\.git", "$Src\.claude", "$Src\claude",
    "$Src\tools\uiverify", "$Src\tools\__pycache__",
    'bin', 'obj', '.vs', '.idea', '__pycache__'
)
$excludeFiles = @('CLAUDE.md', 'imgui.ini', '*.user', '*.binlog', '*.zip')

robocopy $Src $Fresh /MIR /XD @excludeDirs /XF @excludeFiles /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with $LASTEXITCODE" }
$global:LASTEXITCODE = 0   # robocopy's success codes are 0-7; clear it

# Nothing excluded may have survived the copy.
$leaks = @('Brio', 'Ktisis', 'DevHost', 'Norvrandt.Tests', 'CLAUDE.md', 'imgui.ini') |
         Where-Object { Test-Path (Join-Path $Fresh $_) }
if ($leaks) { throw "Excluded paths present in the fresh root: $($leaks -join ', ')" }
Get-ChildItem $Fresh -Recurse -Directory -Filter 'GameData' | ForEach-Object { throw "GameData leaked: $($_.FullName)" }

# The release scaffolding must be there.
@('LICENSE', 'THIRD-PARTY-LICENSES.md', 'README.md', 'Poser.slnx',
  'docs\release\CHANGELOG.md', 'docs\release\exclusions.md') |
  ForEach-Object { if (-not (Test-Path (Join-Path $Fresh $_))) { throw "Missing from fresh root: $_" } }
```

Then append the release-root `.gitignore` block from `exclusions.md` §6, and
make the first commit:

```powershell
Set-Location $Fresh
# Paste the .gitignore block from docs/release/exclusions.md §6 before this line.
Get-Content .gitignore | Select-String -Pattern '^/Ktisis/' -Quiet   # must be True

git init -b main
git add -A

# Existing on disk is NOT the same as tracked. The stock .NET .gitignore's
# `[Rr]elease/` rule matches at any depth and silently swallowed docs\release\
# once already; the Test-Path checks above cannot see that. Ask git.
@('LICENSE', 'THIRD-PARTY-LICENSES.md', 'README.md', 'Poser.slnx',
  'docs/release/CHANGELOG.md', 'docs/release/exclusions.md',
  'docs/release/runbook.md') | ForEach-Object {
    if (-not (git ls-files --error-unmatch $_ 2>$null)) {
        throw "Present on disk but NOT staged — check .gitignore: $_"
    }
}
git status --ignored --short | Select-String '^!!' | Select-Object -First 40
# Read that list. Anything there is a file the public repo will not contain.

git status --short | Measure-Object -Line     # eyeball the file count before committing
git commit -m "Poser $Tag — first public beta"
git log --oneline           # exactly ONE commit, no parents
git rev-list --count HEAD   # must print 1
```

## Step 5 — Gate the fresh root on its own

The public repo has to build for someone who only has the public repo.

```powershell
Set-Location $Fresh
dotnet build Poser.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Fresh root does not build. Something was excluded that should not have been.' }

dotnet test Poser.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers --no-build
if ($LASTEXITCODE -ne 0) { throw 'Fresh root tests failed.' }
```

Note that `bin`/`obj` now exist in `$Fresh` — they are gitignored, and the
verification in step 6 compares tracked files only.

## Step 6 — Diff-verify the fresh root against the tag

Byte-identical minus the exclusions, or stop. This compares git's own blob
hashes, not a text diff, so it catches encoding and line-ending drift too.

```powershell
if (Test-Path $Scratch) { Remove-Item $Scratch -Recurse -Force }
git clone --no-checkout --branch $Tag $Src $Scratch
git -C $Scratch rev-parse HEAD

# Must match what step 4 actually excluded, or this check false-alarms.
# `docs/validation/` is deliberately NOT here: exclusions.md §4 keeps its four
# tracked .md files (the PNGs were never tracked). Uncomment the line only if
# you take the OWNER-DECIDES option and drop them — and then also add
# "$Src\docs\validation" to $excludeDirs in step 4, or the two sides disagree.
$excludedPrefixes = @(
    'Brio/', 'Ktisis/', 'DevHost/', 'Norvrandt.Tests/',
    'PosingCore/Data/GameData/'
    # , 'docs/validation/'
)
$isExcluded = {
    param($p)
    ($p -eq 'CLAUDE.md') -or ($p -eq 'imgui.ini') -or
    ($excludedPrefixes | Where-Object { $p.StartsWith($_) })
}

# "<mode> <type> <sha>`t<path>" for every tracked file, both sides.
$tagFiles = git -C $Scratch ls-tree -r $Tag | Where-Object {
    -not (& $isExcluded ($_ -split "`t", 2)[1])
} | Sort-Object
$newFiles = git -C $Fresh ls-tree -r HEAD | Sort-Object

$delta = Compare-Object $tagFiles $newFiles
if ($delta) {
    $delta | Format-Table -AutoSize
    throw 'Fresh root differs from the tag beyond the exclusion list. STOP — do not push.'
}
"Verified: $($newFiles.Count) tracked files identical to $Tag."
```

`<=` rows are files the tag has and the fresh root lost — either a missing
exclusion entry or a copy bug. `=>` rows are files the fresh root invented.
Both mean stop.

Then read the exclusion delta with your own eyes:

```powershell
$dropped = git -C $Scratch ls-tree -r --name-only $Tag | Where-Object { & $isExcluded $_ }
$dropped   # every line here must be something you meant to drop
```

## Step 7 — Publish, and only the fresh root

Create the empty public repository on GitHub first, with **no** README, license
or .gitignore, so nothing has to be merged.

```powershell
Set-Location $Fresh
git remote add origin https://github.com/<owner>/Poser.git
git remote -v

# Refuse anything that is not the single orphan commit.
if ((git rev-list --count HEAD) -ne 1) { throw 'Fresh root is not a single commit.' }
if (git tag)                            { throw 'Fresh root carries tags. Remove them first.' }

git push -u origin main
```

**Never run any of these against the public remote:** `git push --mirror`,
`git push --all`, `git push --tags`, `git push origin refs/*`, or adding the
public remote to `$Src`. Do not add the public remote to `$Src` at all —
`git push origin --all` from muscle memory in the wrong directory is the exact
mistake this whole procedure exists to make impossible.

If you want the beta tag public, create it fresh on the public repo, on the
public commit:

```powershell
git tag -a $Tag -m "Poser $Tag — first public beta"
git push origin $Tag
```

Then set the repository's About blurb and topics, and confirm GitHub shows
**GPL-3.0** in the sidebar (it reads the root `LICENSE`; the project notice
prepended above the license text does not stop the detector, but check).

## Step 8 — Local cleanup, after the push is confirmed

Only once the public repo is verified. Preconditions first: every lane must be
merged, and the bundle from step 3 must still exist.

```powershell
Set-Location $Src
if (-not (Test-Path $Backup)) { throw 'Backup bundle is gone. Do not delete anything.' }

# Anything not merged into the released head is listed here. Expect nothing.
git branch --no-merged HEAD

git worktree list
git worktree remove C:\tmp\Poser-release-prep
# ...repeat per lane worktree...
git worktree prune

git branch -d codex/release-prep     # -d, never -D: it refuses if unmerged
# ...repeat per lane branch...

git worktree list; git branch
```

The lane history is not lost — it is in `$Backup` and in `$Src`'s own object
store. The public repo simply never contained it.

---

## Checklist

- [ ] 0 — version settled, no `REPLACE-WITH-OWNER` anywhere
- [ ] 1 — all lanes landed; Release build and tests green on a clean tree
- [ ] 2 — annotated tag on the gated commit
- [ ] 3 — dated bundle written outside the repo, verified, copied off-machine
- [ ] 4 — fresh orphan root built; leak checks and scaffolding checks pass; one commit
- [ ] 5 — fresh root builds and tests green on its own
- [ ] 6 — tracked-file diff against the tag is empty; dropped-file list read and approved
- [ ] 7 — empty public repo created; single `git push -u origin main`; no mirror/all/tags
- [ ] 8 — worktrees and lane branches removed with `-d`, bundle retained
