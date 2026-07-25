# Core migration status

## Current boundary

The clean path is split temporarily into:

- `Poser.Domain`: stable identities and pure pose values/rules;
- `Poser.Application`: scene, selection, gestures, commands, pose use cases,
  and the sole transform journal;
- `Poser.Game`: generation-aware bindings, native actor/skeleton/posing
  implementations, lifecycle reconciliation, presentation facades, and live
  acceptance;
- `PosingCore`: transitional entity/state contracts, pose codecs, metadata,
  graphical-map data, and native struct helpers still consumed by runtime.

Domain and application contain no Dalamud, ImGui, pointer, IPC, or legacy
entity references. `Poser.Game` is the anti-corruption boundary.

## Migrated production routes

- stable actor and concrete/virtual-bone selection;
- every retained selection surface (scene tree, Body/Face maps, bone
  matrix, 3D diagram, skeleton overlay) dispatching SelectionId directly to
  SelectionSession — ISelectionService and CleanSelectionServiceAdapter are
  deleted, and no selection mirror events exist;
- actor and bone translate/rotate/scale from gizmo and inspector;
- frozen-baseline multi-target gestures;
- linked-bone and symmetry expansion;
- frozen Parent-pivot rotation through the shared transform gesture;
- actor, bone, region, selection, and whole-skeleton reset;
- bone flip, whole-pose mirror, copy/paste, and in-memory stash;
- pose capture and actor-independent transfer;
- one command-patch undo/redo journal;
- actor/skeleton generation invalidation and GPose teardown.

The active UI uses these facades through stable ids only — the
entity-accepting CleanTransformFacade entry points are deleted, and
frame-scoped spatial reads go through Poser.Game/Viewport/ViewportProjection
(docs/game/viewport-projection.md). The removed legacy `HistoryService` and drag
events no longer form a second journal.

## Transform runtime boundary

`TransformRuntimePort` resolves clean ids directly to the runtime-owned actor
and bone posing implementations while preserving Brio's post-animation pose
application order. Its accepted pre-replacement baseline is live run
`20260724-162755-571` (56/56 executions passed).

The retained compatibility surface is intentionally narrow. Runtime
implementations now live under `Poser.Game/LegacyRuntime`; `PosingCore` retains
only the contracts/storage they still consume:

- GPose, actor discovery/spawn, skeleton discovery, and camera projection;
- animation speed/freeze controls required by posing;
- actor, bone, IK, gaze, and expression pose application;
- pose-file codecs;
- bone metadata, graphical maps, and configuration used by the retained UI.

Camera management, lighting, environment, world objects, reference images,
libraries, projects/autosave, status/VFX, appearance IPC, public IPC, web API,
game-data browsing, and animation browsing have been deleted.

## Acceptance gate

Normal confidence:

```text
/poser test
```

Migration acceptance:

```text
/poser test full
/poser test transform.actor-components --iterations 8
/poser test transform.actor-undo-redo --iterations 8
/poser test posing.bone-components --iterations 8
/poser test posing.animation-interference --iterations 8
/poser test posing.reset-region --iterations 8
/poser test posing.copy-paste-pose --iterations 8
```

The verdict comes from `run.json`, not command duration or chat text. The
clean-core slice was accepted by PBI-001. Later pose-workspace refinements are
reflected directly in the current concept documents.
