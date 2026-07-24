# CleanPoseFacade

## Purpose

`CleanPoseFacade` is a temporary legacy presentation adapter. It accepts
`ISkeleton` and `IBone` values from the current UI, resolves each through
`StableBindingRegistry`, and dispatches stable-id commands to
`PoseEditService`.

Virtual bones are selection-only. A virtual group with a pivot expands to that
concrete pivot for a single-bone command; a group without a pivot is rejected.

The facade performs no pose math and no native mutation.

Whole-pose `Copy`, `Paste`, `Stash`, and `ApplyStash` resolve concrete bones and
delegate to `PoseTransferService`. The facade exposes stash availability and
time for presentation, while the application service owns the snapshot.
