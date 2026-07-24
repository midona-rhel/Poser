# CleanPoseFacade

## Purpose

`CleanPoseFacade` dispatches stable-id pose commands to `PoseEditService`.
Its primary entry points accept stable ids directly:

- `ResetBone(TransformTargetId, string boneName)` and
  `FlipBone(TransformTargetId, string boneName)` — used by the bone context
  menu;
- `ConfigureIk(TransformTargetId, bool enabled)` — per-gesture IK arming
  (session state); the id resolves inside the facade and no entity reaches
  the caller.

The remaining `ISkeleton`/`IBone` overloads are a temporary legacy adapter
for whole-skeleton mirror/reset/region and stash flows: each resolves through
`StableBindingRegistry` before dispatching stable-id commands. Their own
stable-id migration is deferred work outside PBI-001.

Virtual bones are selection-only. A virtual group with a pivot expands to that
concrete pivot for a single-bone command; a group without a pivot is rejected.

The facade performs no pose math and no native mutation.

Whole-pose `Copy`, `Paste`, `Stash`, and `ApplyStash` resolve concrete bones and
delegate to `PoseTransferService`. The facade exposes stash availability and
time for presentation, while the application service owns the snapshot.
