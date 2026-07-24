# TransformGestureService

## Purpose

`TransformGestureService` is the only application entry point for interactive
actor and bone transforms. A gesture captures target state once, evaluates
every update from that frozen baseline, and commits one history patch.

## Port

`ITransformRuntimePort` is implemented by the game adapter:

- `Capture(target)` resolves a stable id and returns absolute transform plus
  interactive pose layers and override state;
- `ApplyAbsolute(baseline, desired)` applies a desired absolute transform from
  the captured baseline;
- `Restore(state)` restores an exact captured application state.

All methods return `TransformPortResult`; stale identity, invalid transform,
native unavailability, and identity mismatch are explicit statuses.

## Lifecycle

1. `Begin` validates homogeneous targets and captures every target.
2. `Update` validates a `TransformDelta` and calculates desired transforms from
   the immutable captures.
3. The runtime applies every desired transform. On partial failure, all targets
   restore to the pre-update captures.
4. `Commit` captures final states and appends one `TransformPatch` to history.
5. `Cancel` restores the initial states.

Only one active gesture is allowed. A scene-generation change, target
invalidation, or new selection cancels it.

## Multi-target pivot behavior

- `PerTarget` rotates each target around itself. This is the default for bones.
- `Primary` rotates secondary target positions around the primary target.
- `SelectionCenter` uses the frozen average starting position.
- `Custom` uses a supplied frozen world-space point.

The pivot never comes from a live, already-mutated transform.
Snapshot-mode bone orbit is the same command with `SelectionCenter` or
`Custom` pivot mode; it no longer owns a second incremental orbit session.

## History

`TransformHistory` stores bounded `TransformPatch` values containing complete
before and after target states. Undo and redo use the same runtime `Restore`
path as cancel, so they cannot diverge from normal application semantics.
The history cursor advances only after every restore succeeds; a failed undo or
redo remains available for a later retry.

## Presentation adapters

Gizmo and inspector controls may retain display-only values while a gesture is
active, but they do not retain native baselines or calculate incremental native
writes. They convert the current UI value to a total `TransformDelta` from the
gesture's pointer-down value and dispatch `Update`.

Inspector bone values are parent-local for readability. Its adapter composes
the edited local value into an absolute model transform once, then derives the
domain delta from the frozen model transform. This prevents parent rotation or
scale from being mistaken for a bone-local translation.

Virtual bone/category rows are expanded to concrete pivot bones before
`Begin`; selection-only group identities can never enter a transform command.
When linked editing is enabled, `BoneLinkCatalog` expansion also happens before
`Begin`, so linked bones are explicit atomic targets rather than native
side-effects.
Copy/mirror symmetry is represented by a per-target `TransformDeltaMode`.
Mirrored partners enter the same capture, rollback, commit, and history patch.
When IK is armed, the game adapter evaluates the resulting clean translation
layer through its native IK solver. IK configuration is session state; the
gesture and its history still contain only stable targets and pose layers.
