# Precision transform input

## Purpose

Precision transform input is the shared interaction contract for position, rotation, and scale axis wells in the Pose inspector. It gives actor and bone transforms both fast pointer adjustment and exact keyboard entry without creating a second editor or changing their service boundaries.

## Interaction contract

| Input | Result |
|---|---|
| Horizontal drag | Adjusts continuously using the row's per-pixel step. |
| Mouse wheel | Applies one coarse step (`perPixel × 10`) and commits immediately. |
| Shift + wheel | Applies ten times the normal wheel step. |
| Ctrl + wheel | Applies one tenth of the normal wheel step. |
| Double-click | Replaces the displayed value with an inline numeric field and selects its contents. |
| Enter or focus loss after editing | Applies the typed value and commits it to history. |
| Escape | Cancels the typed edit without changing the transform. |

The hover tooltip advertises these controls at the point of use. Selection changes call `AppShellView.CancelAxisEdit`, preventing an unfinished value from being applied to a different entity.

The X, Y, and Z prefixes use the exact same 12 px regular monospace face and
baseline as their values. Axis color alone provides the distinction; mixing
Segoe and Cascadia metrics or using lowered subscript styling is intentionally
avoided. Both runs share a 7 px top inset within the 26 px well.

## Ownership and data flow

`AppShellView.ScrubRowDrag` owns only transient widget state: the active axis id, its editable float, and whether the field needs keyboard focus. It reports `changed` and `released` to `PoseInspectorPane`; it does not know which kind of entity is being edited.

`PoseInspectorPane.DrawTransform` retains the semantic responsibility:

1. Read the currently selected entity's transform.
2. Capture the pre-edit transform on the first change.
3. Route live values to `IPosingService`, `IBonePosingService`, or the selected transformable entity.
4. Record one history action when `released` is reported.

Wheel and typed edits use the same apply/commit path as drag gestures, so undo and redo behavior stays consistent.

X, Y, and Z are literal coordinate axes. `PoseMath` accounts for
`Quaternion.CreateFromYawPitchRoll` taking arguments in Y, X, Z coordinate
order, so the red X well does not accidentally edit yaw/Y.

## Reference decisions

- **Brio:** `Brio/Brio/UI/Controls/Editors/PosingTransformEditor.cs` demonstrates axis-colored transform inputs with direct numeric entry. Poser keeps that precision while retaining its narrower custom rail wells.
- **Ktisis:** `Ktisis/Interface/Components/Widgets/TransformTable.cs` treats precise values as part of the primary transform surface instead of a modal workflow.
- The wheel modifiers follow common editor conventions and are intentionally shown in the tooltip rather than occupying permanent rail space.

## Known risks and verification

- The edit state is static because `ScrubRowDrag` is a shared AppShell primitive and the application renders one main shell. A future multi-shell UI should move it into per-shell state.
- Typed values use ImGui's numeric parsing while display values use invariant formatting; verify decimal input under the user's in-game locale.
- In-game verification should cover drag, wheel, both wheel modifiers, Enter, click-away commit, Escape, selection changes during editing, and one-step undo/redo for actors and bones.
