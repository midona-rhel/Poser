# Application state

Shared scene/selection/gesture/history state in `Poser.Application`.

- Identities: `ActorId` = lineage + generation; `BoneId` adds slot, partial,
  index, canonical name. Commands need exact generation match — stale is
  `StaleTarget`, never an address/name fallback. Bone groups are
  selection-only and never transform targets.
- `SceneSession` holds the pointer-free snapshot — the UI's only row source;
  `Contains(target)` is every command's staleness guard.
- `SelectionSession` is the sole ordered selection (stable ids, homogeneous,
  incompatible input replaces, first = primary). `TransformTargetResolver`
  yields the effective targets — every selected bone, primary first — and
  inspector and gizmo consume the identical resolution.
- Gestures: Begin captures all baselines once; Update applies TOTAL deltas
  from those frozen captures (no feedback); partial failure rolls back all;
  Commit = one `TransformPatch`; Cancel restores. One at a time.
- One `TransformHistory`; undo/redo use the same restore path as cancel.
  Discrete edits share it and are rejected during a gesture.
- `PortablePose`: actor-independent layer snapshot matched by
  `(slot, partial, name)`; named producer layers never transfer.
