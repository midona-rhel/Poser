# Linked bones

## Purpose

`LinkedBones` is a small Anamnesis-compatible catalog for controls that should
receive the same interactive pose delta:

- the left and right eye bones;
- the mutually exclusive Viera ear-variant chains on each side.

It is not general left/right symmetry. General `_l`/`_r` copy and mirror
behavior belongs to `IEditorState.SymmetryMode` and the gizmo symmetry path.

## Data flow

`LinkedBones.GetLinks(boneName)` returns the other catalog names in the same
set. `BonePosingService.ApplyTransform` resolves those names in the source
bone's own Havok partial, transfers the same additive position/scale delta and
local quaternion delta, and uses a re-entrancy guard to prevent link cycles.

`IBonePosingService.LinkedBonesEnabled` is a session toggle. The Pose inspector
labels it as eye/Viera-ear linking and its count pill reflects only catalog
members that actually resolve on the current skeleton.

## Identity rule

Resolution uses `(BoneName, PartialId)`. A name-only lookup can find an
unrelated body, face, hair, weapon, or accessory bone when names repeat across
partials.

## Verification

Select one eye and edit it with linking enabled; both eyes should receive one
delta and the inspector pill should show `2`. Disable linking and repeat; only
the selected eye should change. On Viera, repeat with an ear variant and confirm
only existing same-partial variants participate. A normal paired limb such as a
hand must not display a linked count or move through this feature.
