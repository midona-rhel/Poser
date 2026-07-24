# Architecture overview

## Direction

Poser is moving from a feature-service collection into three product
boundaries:

```text
Poser.Core       pure posing state, rules, sessions, commands, and history
      ↓
Poser.Runtime    FFXIV discovery, bindings, hooks, native writes, and live tests
      ↓
Poser            composition plus the focused UI
```

`Poser.Domain` and `Poser.Application` currently form the clean core.
`Poser.Game` is the temporary anti-corruption runtime. `PosingCore` remains only
as a legacy native dependency while behavior is migrated behind runtime ports.
The clean projects are not mechanically merged until `PosingCore` has been
removed, because their current references enforce the intended direction.

The former Norvrandt renderer and Crystarium widget projects are now one
physical `Poser.UI` project. Product windows and panes remain in the plugin
project because they compose game/application services; reusable rendering,
layout, glass, typography, icon, and input primitives live in `Poser.UI`.
Poser does not maintain a general-purpose UI framework plus a second widget
library.

## Ownership

### Core

Owns stable identities, transforms, pose layers, selection and scene sessions,
gesture snapshots, command results, and the sole undo/redo journal. It has no
Dalamud, ImGui, pointer, IPC, or file-system dependency.

### Runtime

Owns actor/skeleton discovery, generation-aware bindings, framework-thread
access, skeleton update hooks, native animation baselines, final pose writes,
and the focused live harness. Raw addresses never escape this boundary.

### Plugin and UI

Owns dependency composition, commands, configuration presentation, the main
window, settings, skeleton viewport canvas, and gizmo viewport canvas. UI
projects application state and dispatches commands; it never calculates a
persistent pose delta or maintains a second history/selection model.

### Infrastructure, when reintroduced

Pose codecs, configuration storage, reports, and external integrations are
adapters around application workflows. They do not mutate native state
directly. A physical infrastructure project is not created unless there is a
real independent consumer.

## Migration invariants

- Preserve Brio-style post-animation pose application.
- Resolve stable ids at command execution time and reject stale generations.
- Capture one frozen baseline per gesture and commit one patch.
- Use one history journal.
- Remove UI consumers before deleting their services.
- Keep the focused live harness green after each native migration slice.
- Do not preserve deferred features through generic abstractions.
