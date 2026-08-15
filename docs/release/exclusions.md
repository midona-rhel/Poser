# Release exclusion manifest

What must **not** reach the public repository. Nothing here is deleted by this
document — it is the checklist the fresh-root build in [runbook.md](runbook.md)
filters against, and the `.gitignore` block at the bottom is what keeps the new
root clean afterwards.

Inspection basis: the `codex/release-prep` worktree (tracked files) plus the
organizer checkout `C:\Users\Midona\OneDrive\Dokument\GitHub\Poser` (`git status
--ignored`), which is the working tree that actually carries the untracked
material.

---

## 1. Reference clones

| Path | Verdict | Why |
|---|---|---|
| `Brio\` (nested inside the Poser checkout, ~139 MB, `origin https://github.com/Etheirys/Brio.git`) | **EXCLUDE** | A full clone of another project, including its `.git`. Publishing it would republish Brio's source and history under Poser's repository. It is a read-only diffing reference, never a submodule and never built. Already covered by the `/Brio/` line in `.gitignore`. |
| `Ktisis\` — **the Ktisis clone directory is named `Ktisis`** and lives at `C:\Users\Midona\OneDrive\Dokument\GitHub\Ktisis` (`origin https://github.com/ktisis-tools/Ktisis.git`, head `e6b3dd41`) | **EXCLUDE — but nothing to do inside the repo** | It is a *sibling* of the Poser checkout, not nested inside it, so no repo path carries it. The exclusion matters only if the fresh root is ever assembled by copying from the parent `GitHub\` directory instead of from the Poser checkout. Do not do that. A defensive `/Ktisis/` line is in the `.gitignore` block below. |

Both clones are GPL-3.0 (see `THIRD-PARTY-LICENSES.md`); the objection is
republication and repository bloat, not license incompatibility.

## 2. Game-derived data

| Path | Verdict | Why |
|---|---|---|
| `Poser.Core/Data/GameData/` (currently `WorldObjectPaths.json.gz`, ~444 KB) | **EXCLUDE** | Extracted FINAL FANTASY XIV game data. Not Poser's to redistribute, and not needed to build. Already gitignored. |

## 3. Local-only and session artifacts

| Path | Verdict | Why |
|---|---|---|
| `CLAUDE.md` | **EXCLUDE** | Agent operating instructions, references private global rules. Already gitignored. |
| `.claude/`, `claude/`, `.claude/settings.local.json` | **EXCLUDE** | Agent/session configuration. Already gitignored. |
| `imgui.ini` | **EXCLUDE** | One developer's ImGui window layout. Already gitignored. |
| `tools/__pycache__/`, `tools/uiverify/` | **EXCLUDE** | Python bytecode and the local UI-verification scratch area. Already gitignored. |
| `docs/validation/**/*.png` | **EXCLUDE** | Captured screenshots from verification runs. Already gitignored. |
| `bin/`, `obj/` under every project | **EXCLUDE** | Build output. Already gitignored. |
| `DevHost/` | **EXCLUDE** | Present on disk in the organizer checkout, ignored there, and tracked on no branch reaching this release. It is a UI harness, not a shipped project, and is absent from `Poser.slnx`. |
| `Norvrandt.Tests/` | **EXCLUDE** | Same situation: on disk in the organizer checkout, ignored, not in `Poser.slnx`, not tracked here. |
| Sibling worktrees `C:\tmp\Poser-*`, `Poser-pbi-007\`, `Poser-pbi-100\` | **EXCLUDE** | Lane worktrees. Outside the repo root; listed so the fresh-root copy is never taken from a parent directory. |
| `C:\tmp\Poser-convergence-reports\` | **EXCLUDE** | Inter-session reports. Outside the repo; never copy it in. |

## 4. Tracked files reviewed and **kept**

Judged, not skipped. Every one of these is tracked on `codex/release-prep` today.

| Path | Verdict | Why |
|---|---|---|
| `docs/validation/*.md` (4 files: backend-maintainability audit, code-health audit + remediation plan, feature-gap audit) | **KEEP** | These read as session artifacts by their dated filenames, but they are Poser's own engineering analysis and the parity checklist cites all four as its inherited basis. Removing them orphans those citations. They contain no credentials, no third-party source and no personal data. **OWNER-DECIDES** if you would rather the public repo not carry dated internal audits — the only cost of excluding them is four broken references in `docs/brio/parity-checklist.md`. |
| `AGENTS.md` | **KEEP** | Contributor-facing documentation policy (one normative home, no per-class docs, Brio/Ktisis consultation rule). It also states the Debug-build auto-deploy hazard, which a contributor genuinely needs. It names an internal task-orchestration workflow; that is a disclosure choice, not a leak. |
| `docs/backlog/PBI-*.md` | **KEEP** | Product backlog and design history. Public repos carry these routinely. |
| `docs/brio/parity-checklist.md`, `docs/brio/known-brio-bugs.md` | **KEEP** | Poser's own comparison notes. No upstream source is reproduced. |
| `docs/architecture/`, `docs/features/`, `docs/process/`, `docs/README.md` | **KEEP** | The normative documentation set. |
| `tools/Test-PoserLiveRun.ps1`, `tools/count-lines.ps1` | **KEEP** | Developer scripts, no secrets. |
| `Poser.Core/Data/RestPoses/*.pose`, `Data/BoneCategories/BoneCategories.json`, `Poser.Game/Data/Festivals.json`, `Poser.Game/Data/props.json` | **KEEP** | Upstream GPL-3.0 data files that Poser embeds and *must* ship for the plugin to work. Attributed per file in `THIRD-PARTY-LICENSES.md`; the copyleft is honoured because Poser is GPL-3.0-only. |
| `Poser/LICENSE` | **KEEP (corrected)** | Was an MIT notice contradicting the root license; now a GPLv3 notice pointing at the root `LICENSE`. |
| `Poser/README.md` | **KEEP (fixed 2026-08-15)** | Was a one-line stub (`# Poser`). Now says what the project directory is and points at the root README, `AGENTS.md` and `docs/`. |

## 5. Pre-publish placeholders that must be replaced

Not exclusions, but they fail the release the same way if they ship as-is.

| Where | Placeholder |
|---|---|
| `Poser/Poser.json` → `RepoUrl` | `https://github.com/REPLACE-WITH-OWNER/Poser` |
| `Poser/Poser.json` → `IconUrl` | `https://raw.githubusercontent.com/REPLACE-WITH-OWNER/Poser/main/images/icon.png` — and the icon file itself does not exist yet. Add `images/icon.png` (Ktisis and Brio both point `IconUrl` at a raw file in their own repo) or delete the field; a 404 icon is worse than none. |

The runbook has a hard gate that greps for `REPLACE-WITH-OWNER` before tagging.

---

## 6. `.gitignore` addition block for the fresh root

The existing `.gitignore` already covers build output, `/Brio/`, `CLAUDE.md`,
`.claude`, `imgui.ini`, `tools/__pycache__/`, `tools/uiverify/`,
`docs/validation/**/*.png` and `Poser.Core/Data/GameData/`. Append this block so
the new root also refuses everything the fresh-root build was built to leave out,
even if someone later drops a clone or a harness back into the tree.

> **The `[Rr]elease/` trap.** The stock .NET `.gitignore` ignores a directory
> named `Release` at *any* depth, not just build output — so it silently
> swallowed `docs/release/` and every file in this directory. The negation
> `!/docs/release/` is already in this repo's `.gitignore` and is repeated in
> the block below; it must name the *directory*, because git never descends into
> an excluded directory and a `!/docs/release/*.md` line would therefore never
> be reached. If you rewrite `.gitignore` for the public root, keep it — without
> it the release documentation exists on disk, passes a `Test-Path` check, and
> is never committed.

```gitignore
# --- Release-root exclusions (added at first public release) ---

# Un-ignore the release docs. `[Rr]elease/` above matches at any depth and
# would otherwise drop this whole directory. Must name the directory itself.
!/docs/release/

# Read-only reference clones. Never publish another project's source or history.
/Brio/
/Ktisis/
/Anamnesis/

# Development harnesses and scratch projects: not in Poser.slnx, not shipped.
/DevHost/
/Norvrandt.Tests/

# Agent/session material.
CLAUDE.md
AGENTS.local.md
/.claude/
/claude/
.claude/settings.local.json

# Local developer state.
/imgui.ini
*.user
.vs/
.idea/

# Verification scratch and captures.
/tools/__pycache__/
/tools/uiverify/
/docs/validation/**/*.png
/docs/validation/**/*.gif

# Game-derived data. Not ours to redistribute.
/Poser.Core/Data/GameData/

# Packaged plugin output.
*.zip
/dist/
```
