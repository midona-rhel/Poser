# PBI-012 — Zero-propagation pose-layer commit safety

## Control

| Field | Value |
|---|---|
| Status | Implementation present; live acceptance pending (status corrected 2026-08-14) |
| Size | Small, urgent runtime defect |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | Record from the clean accepted head before implementation |
| Feature branch | `feature/pbi-012-zero-propagation-commit-safety` |
| Accepted head | Pending |

Status note 2026-08-14: the domain fix is in at the integration head —
`Poser.Domain/Posing/PoseLayers.cs` accepts `TransformComponents.None` and
rejects unknown bits with a typed error (`(value & ~All) == None` known-bit
check; "Transform components contain unknown bits."). The in-game acceptance
walkthrough below remains the user's open gate.

## Defect

Releasing a bone-rotation gesture can throw from `BonePose` while the final
state is captured:

```text
ArgumentException: Pose contains an invalid layer.
BonePose..ctor → TransformRuntimePort.CaptureBone
→ TransformGestureService.Commit → PoseInspectorPane.CommitRotation
```

The retained Parenting controls allow Position, Rotation, and Scale propagation
to all be off. That is a meaningful pose state: edit the selected bone without
propagating any component to its descendants. The legacy stack records it as
`TransformComponents.None`, but `PoseLayer.IsValid` currently rejects `None`.
`CaptureBone` then constructs `BonePose` from that legitimate stack and lets the
constructor exception escape through the UI draw.

## Outcome

Zero propagation round-trips through capture, gesture history, undo/redo, and
restore as a valid authored layer. Invalid transforms or unknown component bits
fail through `TransformPortResult` with target and layer context; no malformed
runtime state throws through an inspector or world-gizmo draw.

## Contract

- `TransformComponents.None` means **apply to this bone, propagate nothing**.
  It is valid for an authored or named pose layer.
- A component mask is valid only when it contains no bits outside
  `TransformComponents.All`; `None` and every subset of `All` are valid.
- Domain and legacy conversions preserve the mask exactly. They must not turn
  `None` into a default, drop the layer, or redirect it to another component.
- Named expression, gaze, constraint, and runtime layers remain excluded from
  manual gesture history exactly as today. This PBI does not change their
  ownership or evaluation order.
- A transform runtime-port method returns a failure result for invalid captured
  data. Exceptions from value-object construction or conversion do not cross
  the port boundary.
- If final capture fails, `TransformGestureService.Commit` restores every frozen
  Before state once, clears the active gesture, appends no history item, and
  returns the specific failure. Existing once-only UI cancellation remains.

## Implementation requirements

1. Replace the `Propagation != None` validity rule with an explicit known-bit
   mask check in the domain. Keep the existing id, finite-delta, and non-zero
   quaternion requirements.
2. Validate legacy component masks during `TransformRuntimePort` capture before
   constructing `PoseLayer`/`BonePose`. Report the exact target, stack index,
   and rejected value when the mask or delta is invalid.
3. Make `CaptureBone` assemble the complete result before returning it; a bad
   stack entry must not partially mutate pose state or history.
4. Preserve `None` through `ToDomainComponents`, `ToLegacyComponents`,
   `BonePose.InteractiveOnly`, `ToLegacyLayers`, history patches, and restore.
5. Keep the fix in the shared domain/runtime path. Do not catch this exception
   in `PoseRailPane`, `PoseInspectorPane`, or `MainWindow`.
6. Add a focused regression scenario to the existing in-game harness if it can
   drive propagation state without new infrastructure. The scenario must report
   the selected `BoneId`, component mask, layer count, gesture id, commit result,
   and history change.
7. Add only a tight source comment for the non-obvious `None` meaning. Update
   the existing posing/gesture normative document only if its contract needs the
   clarification; create no new architecture document.

## In-game acceptance

Use one normal body bone with descendants and repeat the zero-propagation case
through each retained edit surface:

1. Turn Parenting Position, Rotation, and Scale off.
2. Rotate using the inspector ring, release, and confirm no exception.
3. Undo and redo; the bone returns exactly and descendants do not inherit the
   edit.
4. Repeat with a numeric rotation well and the world rotation gizmo.
5. Repeat commit/cancel eight times; each commit adds exactly one history item,
   each cancel adds none, and no gesture remains active.
6. Exercise all eight Position/Rotation/Scale mask combinations and confirm each
   commits and restores without changing the chosen mask.
7. Repeat while expression and gaze layers are active. They remain applied and
   are neither captured into manual history nor erased by undo/redo.
8. Confirm a deliberately invalid component bit, through the focused test seam,
   produces one reported port failure, exact rollback, no history entry, and no
   exception escaping the framework update.

## Excluded

- No redesign or restyling of the Parenting footer.
- No pose-layer architecture rewrite or change to layer evaluation order.
- No changes to expression, gaze, IK, animation, mirror, or file formats.
- No generic unit-test framework, standalone UI, IPC automation, or screenshot
  validation.
- No PBI-011 component-conformance changes.

## Handoff

Report the exact base/head range, the layer that reproduced the defect, the
domain invariant changed, runtime failure mapping, history/rollback behavior,
focused scenario output, Release validation, `git diff --check`, and the
remaining in-game checklist. Compilation alone is not runtime acceptance.
