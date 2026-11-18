# Release exclusions

What must not end up in the repository at a release, or in the release archive. Check both against this list before publishing (step 4 of the [runbook](runbook.md)). The list does not delete anything by itself.

## Leave out

| Path or kind | Why |
|---|---|
| `bin/`, `obj/`, `dist/`, `artifacts/`, `TestResults/`, `*.zip` | Build and test output. |
| `*.log`, `*.dmp`, `*.mdmp`, `*.core`, `*.etl` | Logs and crash dumps can hold local paths and personal data. |
| `Brio/`, `Ktisis/`, `Anamnesis/` | Copies of other tools kept for reference. They are not Poser's source. |
| Folders not listed in `Poser.slnx`, such as `DevHost/`, `Norvrandt.Tests/`, `Poser.Core/` or `PosingCore/` | Old or local projects. Nothing in the release is built from them. |
| `.claude/`, `claude/`, `CLAUDE.md`, `AGENTS.local.md`, `.local-notes/`, `.worktrees/`, `.tmp/` | Local notes, agent sessions and working copies. |
| `imgui.ini`, `*.user`, `.vs/`, `.idea/` | Local editor and window state. |
| `tools/__pycache__/`, `tools/uiverify/`, screenshots and recordings made while testing | Leftovers from checking changes. |
| `**/Data/GameData/` | Data taken from the game, which Poser may not redistribute. |

## Keep, after a look

- `LICENSE`, `README.md`, `THIRD-PARTY-LICENSES.md` and the files in `docs/release/`.
- Poser's own documentation, as long as it has no local paths, private data, screenshots or copied source from other projects.
- Data from other projects listed in `THIRD-PARTY-LICENSES.md`, when Poser needs it to run and its credit ships with it.

The `.gitignore` rule `[Rr]elease/` for build output also matches `docs/release/`. The `!/docs/release/` line after it keeps this folder tracked; do not remove it.

## Last check

Compare the release commit's files and the release archive with each other. Reject anything unexpected: duplicate or unsafe file names in the archive, links or other special entries, logs or dumps, local paths, passwords or keys, personal data, and material from other projects without its credit. Hide private details in anything you save from the scan.
