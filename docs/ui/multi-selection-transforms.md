# Multi-selection transforms

## Purpose

Multi-selection transforms make Ctrl/Shift scene-tree selection meaningful: editing the primary actor or bone applies the same relative transform change to every selected entity of that type. The inspector remains anchored to one primary value while the complete selection moves as a group.

## Relative-delta contract

`PoseMath.ApplyRelativeDelta(primaryBefore, primaryAfter, secondaryBefore)` transfers a primary edit to another transform:

- Position uses the primary's additive offset.
- Scale uses the primary's additive offset.
- Rotation computes the primary's quaternion delta, moves it through world orientation, and applies it in the secondary transform's local orientation.

This is the same rule previously embedded in `GizmoOverlayWindow`. Moving it to `PoseMath` prevents the gizmo, rotation ball, drag wells, wheel steps, and typed values from disagreeing; the gizmo now calls the shared helper too.

## Inspector session

At the first changed frame, `PoseInspectorPane` snapshots every selected actor or bone of the primary type. On each subsequent frame it:

1. Applies the requested value to the primary.
2. Computes the relative change from the primary's previous value.
3. Applies that change to each secondary entity from its own previous value.
4. Retains the resulting values as the next frame's baseline.

The session is cleared on release, selection change, or cancellation. Bone snapshots and application values are model-space even though the primary rail displays parent-local values.

## History

A multi-entity gesture records one `CompositeAction`, containing one `TransformActorAction` or `TransformBoneAction` per changed entity. One Undo therefore restores the entire group. Single-entity gestures retain the existing individual history action.

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
