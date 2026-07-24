# Verification

## Core rewrite gate

In GPose:

```text
/poser test
```

This runs seven state contracts once. It is the normal confidence check while
rewriting the posing core.

For acceptance:

```text
/poser test full
```

This runs the same seven contracts eight times. Narrow diagnosis remains
available:

```text
/poser test posing.animation-interference --iterations 8
/poser test transform.actor-components --iterations 8
/poser test status
/poser test cancel
```

`run.json` is the verdict. `Succeeded` means every expected execution completed
with no failed or skipped rows. Eight iterations additionally set
`AcceptanceQualified`.

## UI review

UI approval happens manually in the running plugin. There is no standalone
host, npm/browser renderer, screenshot, or pixel-diff gate.

UI changes are presented to the user in game. Compilation proves only that the
plugin still builds; it is not visual approval.

## Feature diagnostics

Camera, lights, world objects, environment, persistence, appearance, IPC, and
other product features are not part of `/poser test full`. Add a focused
feature diagnostic beside that feature when needed; do not grow the rewrite
gate into a second application.

## Artifacts

```text
live-tests/<UTC timestamp>/
  run.json
  events.jsonl
  report.json
  summary.md
  snapshots/*.json
```

Use `tools/Test-PoserLiveRun.ps1` to consume the durable verdict outside the
game.
