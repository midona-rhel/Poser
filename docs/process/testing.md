# Testing

- `/poser test` runs the seven rewrite contracts once (selection,
  actor components, actor undo/redo, bone components,
  animation-interference, reset-region, copy-paste-pose); `full` runs them
  eight times for migration acceptance; a scenario id narrows diagnosis;
  `status`/`cancel` manage runs.
- The harness spawns one controlled clone per iteration, drives production
  command routes, snapshots boundaries, checks shared invariants, restores
  the user's actors and selection. It never opens or judges UI.
- `run.json` is the only verdict (`Succeeded`/`Failed`/`Incomplete`/
  `Cancelled`/`Interrupted`/`RunnerError`; atomically replaced). Only
  `Succeeded` is success; `AcceptanceQualified` additionally requires the
  repetition count. Never infer success from chat text or file existence.
- `tools/Test-PoserLiveRun.ps1` reads the verdict outside the game
  (exit 0 success, 1 failure, 2 running, 3 invalid).
- Artifacts per run: `live-tests/<UTC>/run.json`, `events.jsonl`,
  `report.json`, `summary.md`, `snapshots/`.
- UI approval is manual, in game. No npm, browser, screenshot, pixel-diff,
  or standalone-host testing exists or may be added.
