# Live test run report

## Purpose

`LiveTestRunReport` is the single authoritative answer to whether one in-game
test invocation succeeded. Scenario event lines and snapshots are diagnostic
evidence; callers must not reconstruct a run verdict by counting those files.

Every accepted invocation creates `run.json` before its first game action. The
file starts with outcome `Running`, is atomically checkpointed after every
durable scenario result, and is atomically replaced with exactly one terminal
outcome:

- `Succeeded` — every selected scenario execution completed and passed;
- `Failed` — at least one setup, scenario, invariant, cleanup, or coverage
  assertion failed;
- `Incomplete` — execution reached the end but one or more selected assertions
  were skipped or the expected execution count was not reached;
- `Cancelled` — the user explicitly cancelled the run;
- `Interrupted` — plugin unload, reload, game termination, or recovery of a
  stale `Running` report interrupted the run;
- `RunnerError` — the harness itself threw outside an isolated scenario.

Only `Succeeded` makes `IsSuccessful` true. `AcceptanceQualified` is a stricter
and separate statement: it also requires the configured repetition requirement.
A successful one-cycle smoke run is therefore trustworthy as a smoke result but
is not mislabeled as full acceptance.

## Durable lifecycle

`LiveTestRunStore` owns the artifact contract:

1. create the run directory and atomically write a `Running` report;
2. replace `run.json` after each result so a crash retains confirmed progress;
3. replace it with the terminal report before writing optional human summaries;
4. when a new plugin instance starts, convert any latest stale `Running` report
   to `Interrupted`.

Atomic replacement uses a sibling temporary file followed by a same-volume
rename. Consumers either observe the previous complete JSON document or the new
complete document, never a partially-written verdict.

`report.json` remains a terminal compatibility copy. `run.json` is the
authority. `events.jsonl` remains append-only forensic evidence for locating the
last native boundary reached.

## Public use

`ILiveTestService.RunAsync` returns `LiveTestRunReport`, and `LastRun` exposes
the latest persisted report across plugin reloads. `/poser test status` reports
its exact outcome, success flag, acceptance qualification, counts, termination
detail, and artifact directory.

The repository-side reader uses the same authority file:

```powershell
.\tools\Test-PoserLiveRun.ps1
.\tools\Test-PoserLiveRun.ps1 -Path <run-directory>
```

It exits `0` only for `Succeeded`, `1` for a non-success terminal outcome, `2`
while a run is still `Running`, and `3` for missing or invalid artifacts. This
lets Codex, CI, and local scripts consume the verdict without parsing chat or
Markdown. The reader also rejects contradictory reports, such as `Succeeded`
with failed/skipped rows or an incomplete execution count.

When inspecting artifacts produced before `run.json` existed, the reader can
normalize the terminal legacy `report.json` count fields into the same output.
It reports `sourceFormat=legacy-compatibility` in JSON mode. This is read-only
compatibility for existing evidence; every new run must produce and prefer
`run.json`.

Command completion must print the report outcome. It must never infer success
from task completion, an empty failure list, or the existence of `summary.md`.

## Reference comparison

Brio and Ktisis do not contain a comparable reusable live acceptance runner;
their production posing behavior remains the scenario oracle reference. Poser
therefore owns this persistence and verdict contract rather than copying an
ad-hoc reference implementation.
