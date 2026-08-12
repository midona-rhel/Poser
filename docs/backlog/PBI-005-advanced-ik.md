# PBI-005 — Advanced IK configuration

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Size | Large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-005-base` |
| Feature branch | `feature/pbi-005-advanced-ik` |
| Accepted head | `63816eb` |

## Outcome

Keep the current selected-bone **Live IK** switch as the simple default, and
add Brio/Ktisis advanced configuration beneath it. Hands and feet can use Two
Joint or CCD solving with configurable constraints, target mode, gain,
iterations, joint weights, hinge limits, and hinge axis. Translation from the
inspector or world gizmo moves the IK target; rotation and scale do not start
an IK solve.

## Reference and product decisions

- Brio is authoritative for its per-bone advanced-IK contract:
  `BonePoseInfo.DefaultIK`, enabled state, solver selection, constraint
  enforcement, CCD depth/iterations, native Havok calls, and apply ordering.
  `EnforceConstraints` means solve first, then restore the requested end-bone
  translation when constraints are off.
- Ktisis supplies the additional controls and chain detail absent from Brio's
  editor: CCD gain, Two Joint fixed/relative target mode, per-joint gains,
  hinge range/axis, twist bones, and optional end rotation.
- Retain Poser's Picto-style inspector. Configuration is for the primary
  selected bone only; do not add Ktisis's actor-wide group panel or a popup.
- IK remains session-only. It is not exported, stashed, or added to transform
  history. Undo/redo continues to store pure authored transform deltas.

## Supported chains and identity

The existing four endpoints remain the user-facing chains:
`j_te_l`, `j_te_r`, `j_asi_d_l`, and `j_asi_d_r`. Resolve every chain member
inside the endpoint's exact `SkeletonId` and partial; never fall back to
Character or another slot.

- Arm: `j_ude_a_*`, optional `n_hkata_*`, `j_ude_b_*`, optional
  `n_hhiji_*`, endpoint `j_te_*` (`j_hand_*` is a Ktisis-compatible alias).
- Leg: `j_asi_a_*`, `j_asi_b_*`, optional `j_asi_c_*`, endpoint
  `j_asi_d_*` (`j_foot_*` is a Ktisis-compatible alias).

Two Joint is offered only when its mandatory chain resolves exactly. CCD
walks same-slot parents from the endpoint and clamps depth to the available
chain. Missing optional twist bones use native index `-1`.

## Configuration contract

Replace the mutable legacy configuration with one validated, stable-id
configuration containing both solver settings so switching solver does not
discard tuning:

- Enabled; Enforce Constraints; solver `Two Joint | CCD`.
- CCD: depth `1..20`, iterations `1..60`, gain `0..1`.
- Two Joint: target `Relative | Fixed`; first/second/end gain `0..1`;
  hinge minimum/maximum `0..180°`; non-zero normalized hinge axis; optional
  end-rotation enforcement.

Defaults preserve current behavior: Two Joint, Relative, constraints on,
gains `1`, full `0..180°` hinge range, arm axis `+Z`, leg axis `-Z`, end
rotation off. CCD defaults to depth `3`, iterations `8`, gain `0.5`.
Invalid or non-finite values are rejected before the native boundary.

Disabling retains tuning but clears any fixed-target capture. **Reset
defaults** restores the selected chain's defaults while preserving its
Enabled state. Reset Bone retains IK configuration; Reset All disables and
clears every chain configuration.

## Target semantics

- Relative is today's behavior: each frame targets the animated endpoint
  position plus its authored translation delta, so the target follows
  animation.
- Fixed holds the endpoint in exact skeleton model space. Entering Fixed or
  enabling a Fixed chain captures `(effective target, authored translation)`;
  later targeting uses `captured target + (current translation − captured
  translation)`. Switching modes therefore never jumps or double-applies an
  existing edit, while undo/redo still moves the fixed target correctly.
- Disabling, Reset All, actor loss, or exact skeleton replacement clears the
  fixed capture. A replacement never inherits configuration or targets.
- When end rotation is enabled, pass the requested rotation to the Two Joint
  solver and do not apply it a second time afterward. When disabled, retain
  the current direct authored-rotation behavior.

Configuration changes during an active transform gesture are rejected with a
clear log reason. Selection changes keep the existing once-only gesture
cancellation behavior.

## Native/runtime requirements

- Keep solver allocation and all unsafe access in `Poser.Game`; Application
  and UI exchange stable ids and immutable validated values only.
- Initialize every native CCD and Two Joint field on every solve. Shared
  native buffers must not leak gain, indices, axis, limits, enforcement, or
  targets from the previously solved chain.
- CCD constructor receives the configured gain rather than the current
  hard-coded `1`.
- Two Joint writes mandatory/optional indices, three gains, cosine-converted
  hinge limits, normalized local hinge axis, target position/rotation, and
  enforcement flags before calling Havok.
- Preserve PBI-004 slot-qualified pose storage, lifecycle purge, finalization
  order, and live reapplication. A failed or unavailable solver must not emit
  non-finite transforms or partially rewrite configuration.

## Inspector behavior

Use the existing collapsible **IK** section and retained controls:

- First row: Live IK switch and right-aligned Reset Defaults action.
- Solver uses the shared compact combo.
- Two Joint shows Target `Relative | Fixed`, Constraints and End Rotation
  switches, anatomical gains (Shoulder/Elbow/Hand or Hip/Knee/Foot), hinge
  Min/Max, and X/Y/Z axis wells.
- CCD shows Constraints, Depth, Iterations, and Gain.
- Unsupported bones show one quiet unavailable row. No wrapped explanation,
  nested disclosure, separate window, context popup, raw bone indices,
  invented glyphs, or horizontal overflow.

Controls use existing spacing, wells, switches, combos, and disabled styling.
The section reports its measured height exactly and must not create a
permanent scrollbar.

## Architecture and documentation

UI reads and writes configuration through one stable-id application path; it
must not call `GetBoneIK`/`SetBoneIK` with retained entities. Delete the
superseded boolean-only facade and duplicated mutable state after migration.
Extend only `docs/features/expression-gaze-and-ik.md`; put native conversion
in tight source comments. Do not create class/service documentation.

## Excluded

- Arbitrary user-authored chains, tail/IVCS groups, actor-wide bulk arming,
  IK animation/keyframes, persistence, or pose-file schema changes.
- A new window, test framework, DevHost, npm, IPC, screenshots, or automated
  visual validation.

## Implementation order

1. Define validated configuration, chain definitions, and stable-id port.
2. Migrate per-exact-skeleton session storage and Reset All behavior.
3. Implement complete CCD/Two Joint native mapping and fixed targets.
4. Wire inspector/world transform paths to the shared target semantics.
5. Build the advanced IK inspector and remove legacy configuration paths.
6. Update the one canonical document and perform cleanup.

Each step is a reviewable commit. Do not amend or rebase after review starts.

## Acceptance

- Existing Live IK defaults behave unchanged on all four limbs.
- Two Joint and CCD reach the same requested target from inspector and world
  gizmo without drift, NaN, or cross-slot writes.
- Relative follows a running animation; Fixed remains stationary and changes
  mode without jumping.
- CCD depth, iterations, and gain; Two Joint gains, hinge range/axis,
  constraints, and end rotation each produce a visible, reversible effect.
- Cancel restores the frozen baseline; undo/redo remains one transform patch
  and uses the currently retained IK configuration.
- Switching selected bones shows that bone's own configuration. Disabling,
  Reset Defaults, Reset Bone, Reset All, and skeleton replacement obey the
  state rules above.
- Release builds with zero errors and warnings. All visual/native behavior is
  accepted only through the user's in-game walkthrough.

## Handoff

Report base/head, commit map, changed paths, configuration identity and
validation, exact chain resolution, native field mapping, fixed-target
lifecycle, removed legacy paths, Release build, and remaining in-game checks.
Do not claim runtime or visual correctness from compilation.
