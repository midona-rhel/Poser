# Release exclusion manifest

This is the tracked-tree and package-staging checklist for a release. It does
not delete files or replace the final package scan.

## Exclude

| Path or kind | Reason |
|---|---|
| `bin/`, `obj/`, `dist/`, `*.zip` | Generated build or package output. |
| `*.log`, `*.dmp`, `*.mdmp`, `*.core`, `*.etl` | Diagnostics can contain local state or personal data. |
| `Brio/`, `Ktisis/`, `Anamnesis/` | Reference clones are not Poser source or release inputs. |
| `DevHost/`, `Norvrandt.Tests/` | Local development harnesses outside the shipped solution. |
| `.claude/`, `claude/`, `CLAUDE.md`, `AGENTS.local.md` | Local agent/session material. |
| `imgui.ini`, `*.user`, `.vs/`, `.idea/` | Local editor and window state. |
| `tools/__pycache__/`, `tools/uiverify/`, captured validation media | Verification scratch and live-test evidence. |
| `PosingCore/Data/GameData/` | Game-derived data that Poser cannot redistribute. |

`PosingCore/` is an active source project. Only its `Data/GameData/` subtree is
excluded.

## Keep and inspect

- `LICENSE`, `README.md`, `THIRD-PARTY-LICENSES.md`, and release documentation.
- Poser's own design, validation, and backlog documentation when it contains no
  local paths, private data, screenshots, or copied upstream source.
- GPL-compatible upstream data listed in `THIRD-PARTY-LICENSES.md` when it is
  required at runtime and its attribution remains with the release.

The `[Rr]elease/` ignore rule matches documentation directories too. Preserve
the `!/docs/release/` exception so the runbook and this manifest stay tracked.

## Final checks

Before a release, compare the approved Git tree and the separately staged ZIP.
Reject unexpected files, duplicate or unsafe archive names, non-regular archive
entries, bundled logs or dumps, local paths, credentials, personal data, and
unattributed third-party material. Keep scan output redacted.
