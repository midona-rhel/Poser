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

## Reset All (actor-level)

`ResetAll(ISkeleton)` is the one documented actor-level reset operation
behind the Pose section's **Reset All** button. One activation:

1. clears expression weights and removes the expression layer
   (`IExpressionService.ResetExpression` — runs before the pose stacks are
   cleared so managed weights can never outlive their layers);
2. restores and clears all Poser gaze modes, participating parts, targets,
   and locks (`IGazeService.ResetGaze`, which routes through the native
   pre-Poser restore path);
3. clears manual pose transforms for all skeleton regions
   (`Reset(skeleton, PoseRegion.All)`, history-aware);
4. disarms actor-local IK chains (`SetAllIk(false)`) and turns the Live IK
   session switch off.

It deliberately preserves the actor's world/model placement, the pose
stash/clipboard, UI tool and Local/World choices, and tree disclosure. Every
step runs even when an earlier one fails; failures aggregate into one
returned `PoseEditResult` and one logged warning — the UI never fires several
unrelated callbacks with no failure contract.
