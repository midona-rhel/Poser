# Application state

Shared scene/selection/gesture/history state in `Poser.Application`.

- Identities: `ActorId` = lineage + generation; `SkeletonId` = actor +
  **slot** (Character/MainHand/OffHand/Prop/Ornament) + that slot's own
  generation — slots are independently replaceable skeletons of ONE actor,
  never separate actors, and replacing a weapon bumps only its slot.
  `BoneId` adds partial, index, canonical name and inherits its slot from
  the skeleton id (one slot field, nothing can disagree). Commands need
  exact generation match — stale is `StaleTarget`, never an address/name
  or cross-slot fallback. Bone groups are selection-only, never targets.
- `SceneSession` holds the pointer-free snapshot — the UI's only row source;
  `Contains(target)` is every command's staleness guard.
- `SelectionSession` is the sole ordered selection (stable ids, homogeneous,
  incompatible input replaces, first = primary); a bone selection may span
  slots of the same actor. `TransformTargetResolver`
  yields the effective targets — every selected bone, primary first — and
  inspector and gizmo consume the identical resolution.
- Gestures: Begin captures all baselines once; Update applies TOTAL deltas
  from those frozen captures (no feedback); partial failure rolls back all;
  Commit = one `TransformPatch`; Cancel restores. One at a time.
- One `TransformHistory`; undo/redo use the same restore path as cancel.
  Discrete edits share it and are rejected during a gesture.
- `PortablePose`: actor-independent layer snapshot matched by
  `(slot, partial, name)`; named producer layers never transfer.
