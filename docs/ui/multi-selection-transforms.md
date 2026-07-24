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

One shared rule in one service prevents the gizmo, rotation ball, drag wells,
wheel steps, and typed values from disagreeing.

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

- Actor edits affect selected actors only.
- Bone edits affect selected bones only.
- Mixed selections remain valid, but the inspector transforms only entities matching its primary type.
- Virtual bones remain a gizmo-specific aggregate and are excluded from direct numeric transform sessions because they cannot accept transforms themselves.
- During an explicit multi-bone gesture, implicit linked-bone propagation is paused so a selected pair cannot receive the same delta twice. Single-bone gestures retain normal linked editing.
- Gizmo expansion, root filtering, and symmetry pairing key real bones by
  `(BoneName, PartialId)`. Name-only identity can merge unrelated body, face,
  hair, weapon, or accessory bones that happen to share a Havok name.

## Reference decisions

- **Brio:** its entity manager supports multi-selection and its posing controls propagate a primary edit across selected targets.
- **Ktisis:** the scene tree selects with modifier modes and `ITransformTarget.Targets` exposes a multi-target transform set to the object editor.
- **Existing Poser gizmo:** its former private secondary-transform helper supplied the proven delta convention now centralized in `PoseMath.ApplyRelativeDelta`.

## Known risks and verification

- Parent-and-child bone selections can intentionally compound through hierarchy updates; compare direct rail edits with the existing gizmo behavior in-game.
- Verify that the temporary linked-edit pause is restored after multi-bone gestures, including exceptional service paths.
- In-game verification must cover two actors and multiple bones for drag, wheel, typed input, rotation ball, and one-step undo/redo.
