# Live test service

## Purpose

`LiveTestService` is the small in-game gate for the clean posing rewrite. It
does not test every Poser feature and does not host UI prompts. It proves seven
production contracts against a controlled actor:

1. actor → bone → clear selection;
2. isolated actor translation, rotation, and scale;
3. actor undo/redo;
4. isolated bone translation, rotation, and scale;
5. persistent bone rotation over an unfrozen native animation baseline;
6. atomic pose reset;
7. actor-independent portable pose capture/apply.

Actor creation, skeleton readiness, snapshot invariants, actor destruction, and
selection restoration are harness setup/cleanup, not additional scenarios.

## Public API

`ILiveTestService` exposes `IsRunning`, `LastRunDirectory`, `LastRun`,
`Cancel()`, and `RunAsync`. `RunAsync` returns the authoritative
`LiveTestRunReport`.

```text
/poser test
/poser test full
/poser test posing.animation-interference --iterations 8
/poser test status
/poser test cancel
```

Bare `test` runs all seven contracts once. `full` runs the same focused gate
eight times. A group or stable scenario id narrows it.

## Dependencies

The service depends only on framework/GPose state, actor spawn/pose, skeleton
and bone posing, animation, selection, and the clean transform/pose facades.
It has no camera, light, environment, world-object, persistence, game-data,
appearance, IPC, or UI dependency.

Validation remains an internal `Poser.Game/Validation` adapter. A separate
validation project is deferred until an external host needs it.

## Evidence

Each action is bracketed by actor/selection/skeleton snapshots. Invariants check
finite transforms, native and layer quaternion rules, unique stable identities,
unchanged skeleton identity, controlled cleanup, and selection restoration.
`run.json` is the authoritative verdict; see
`docs/validation/live-test-run-report.md`.

## Brio/Ktisis reference

Brio supplies the live-animation pose ordering used by the animation
composition oracle. Ktisis supplies interaction expectations but has no
equivalent reusable live harness. UI behavior is reviewed manually in game.
