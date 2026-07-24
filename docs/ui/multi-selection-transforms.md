# Multi-selection transforms

## Purpose

Multi-selection transforms make Ctrl/Shift scene-tree selection meaningful: editing the primary actor or bone applies the same relative transform change to every selected entity of that type. The inspector remains anchored to one primary value while the complete selection moves as a group.

## Relative-delta contract

A gesture carries one domain `TransformDelta` derived from the primary's
frozen baseline. `TransformGestureService.Update` applies that delta to every
frozen target baseline (position additive, rotation composed, scale
multiplicative, per-target `TransformDeltaMode` for mirrored partners). The
primary receives its absolute edited value by construction; secondaries
receive the delta relative to their own frozen baselines — never the primary's
absolute value.

One shared rule in one service prevents the gizmo, rotation rings, drag wells,
modifier drags, and typed values from disagreeing.

## Inspector session

The inspector opens one gesture per field interaction. At pointer-down (or
typed-edit start) it dispatches `Begin` with the `TransformTargetResolver`
effective target list (selected descendants removed; the first surviving root
in original selection order is the effective primary and display source); each frame it converts the current UI
value into a total `TransformDelta` from the gesture's pointer-down value and
dispatches `Update`; release commits, Escape cancels. The gesture service owns
the frozen baselines — the pane retains only display values.

Selection change, tool/space change, and scene invalidation cancel the active
gesture before the new target is shown. Bone values display parent-local; the
pane composes the edited local value to an absolute model transform once and
derives the domain delta from the frozen model transform.

## History

A completed gesture — single- or multi-target — commits exactly one
`TransformPatch` holding every target's before/after state. One undo restores
the entire group through the same runtime restore path as cancel.

## Selection boundaries

- Actor edits affect selected actors only; bone edits affect selected bones
  only. Mixed selections do not exist: `SelectionSession` keeps the group
  homogeneous, and incompatible input replaces the selection.
- Selection-only group identities never enter a transform command; targets
  are always concrete stable bone ids.
- Linked partners expand into explicit targets before `Begin` and share the
  gesture's atomic capture, rollback, commit, and history patch — there is no
  implicit propagation to pause, so a selected pair can never receive the
  same delta twice.
- Root filtering and symmetry pairing key bones by stable
  `BoneId` (slot, partial, index, canonical name), so duplicate Havok names
  across body, face, hair, weapon, or accessory partials can never merge.
- Mirroring is **counterpart-frame aware** (correction round 3B).
  Counterpart bones' bind/animated baselines can differ by ~180°, so a raw
  component flip turns a forward arm backward. Every transferred authored
  adjustment is evaluated relative to its source bone's frozen animated
  baseline, reflected through the sagittal plane (model-space mirror is the
  Ktisis FlipPose convention `(−x, −y, z, w)`, lateral position z), and
  rebased into the destination baseline's frame:
  `d′ = B_dst⁻¹ · M(B_src) · M(d) · M(B_src)⁻¹ · B_dst`. This applies to
  **Mirror edits** (animation-safe: authored layers only, pairs exchange,
  center bones self-mirror, one atomic history entry), **Flip bone**
  (authored adjustment of one bone; clear no-edit result when untouched),
  and live **Symmetry: Mirror** (both counterpart baselines frozen at
  gesture start; model-frame deltas reflect directly). The separate
  **Bake mirrored pose…** action (actor node only, behind a confirmation)
  mirrors the currently evaluated body pose per Ktisis
  `EntityPoseConverter.FlipPose` — opposite-name rotation exchange,
  positions untouched, face/hair/j_ex partials and iv_/ya_ bones excluded,
  root yaw-corrected and flipped 180° — and materializes it as authored
  state; it may break animation-relative behavior and says so.

## Reference decisions

- **Brio:** its entity manager supports multi-selection and its posing controls propagate a primary edit across selected targets.
- **Ktisis:** the scene tree selects with modifier modes and `ITransformTarget.Targets` exposes a multi-target transform set to the object editor.
- **Existing Poser gizmo:** its former private secondary-transform helper supplied the proven delta convention now centralized in `TransformGestureService.Update`.

## Known risks and verification

- Parent-and-child bone selections both transform (user-requested reversal
  of the PBI-001 descendant filter): each target recomputes absolutely from
  its own frozen Begin baseline, so an ancestor's propagation cannot
  feedback-compound — the descendant's own absolute write lands last within
  the update. Verify rail edits and gizmo edits agree in-game.
- In-game verification must cover two actors and multiple bones for drag, typed input, the rotation gizmo, one-step undo/redo, and Escape cancellation.
