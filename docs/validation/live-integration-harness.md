# Focused live integration harness

## Boundary

The in-game harness validates the clean posing rewrite, not the entire plugin.
It owns controlled actor setup, seven rewrite scenarios, shared transform and
identity invariants, cleanup, and the durable run verdict.

It explicitly excludes UI visuals, camera, lighting, world objects,
environment, persistence, appearance, IPC, browsing, and data-sheet checks.

## Iteration lifecycle

1. Capture the user's actor count and selection.
2. Spawn one controlled player clone and wait for its skeleton.
3. For each selected rewrite scenario:
   - persist a before snapshot;
   - flush the pending-action event;
   - invoke the production application/facade route;
   - wait two framework frames;
   - persist an after snapshot;
   - run the scenario oracle and shared invariants;
   - atomically checkpoint `run.json`.
4. Stop test animation and clear actor overrides.
5. Destroy the controlled clone.
6. Restore the user's selection.
7. Prove actor count and selection match the baseline.

## Shared invariants

- actor ids and `(partial, bone index)` identities are unique;
- transforms contain finite values and non-zero rotations;
- clean pose layers contain unit rotations;
- native Havok rotations are finite/non-zero but need not be exactly unit;
- the controlled skeleton identity does not change during a scenario;
- the clone and all of its rewrite state disappear during cleanup;
- the user's original selection is restored.

## Repetition

Bare `/poser test` executes all seven contracts once. `/poser test full`
executes the same catalog eight times. A direct group/scenario selector may be
used for diagnosis. There is no interactive/automatic split.

## UI verification

Visual approval happens manually in the running plugin. The live harness never
opens, automates, captures, or judges UI.

## Artifacts and verdict

Each run writes `run.json`, `events.jsonl`, `report.json`, `summary.md`, and
boundary snapshots. `run.json` is authoritative and follows
`live-test-run-report.md`.

Plugin unload/reload records or recovers `Interrupted`; user cancellation is
`Cancelled`; harness failures are `RunnerError`. None are successful.
