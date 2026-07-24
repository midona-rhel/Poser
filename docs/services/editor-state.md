# EditorState

`PosingCore/Core/EditorState.cs` — interface `IEditorState` (plus its enums) in `PosingCore/Services/IEditorState.cs`.

## Purpose

Plain property bag for editor-wide *tool* settings — gizmo mode, orientation, overlay display mode, symmetry. Explicitly **not** selection (that is the application `SelectionSession`) and **not** persisted configuration (that is `ConfigurationService`). UI reads/writes properties directly each frame.

## Public API

All get/set auto-properties; defaults in parentheses.

| Property | Type | Meaning |
|---|---|---|
| `TransformOrientation` | `TransformOrientation` (`Local`) | Gizmo axes: `Local` or `Global` |
| `TransformTool` | `TransformTool` (`Rotate`) | `Move`, `Rotate`, `Scale`, `Universal` |
| `DebugMode` | `bool` (`false`) | Expands all entities, logs untranslated bones |
| `BoneDisplayMode` | `BoneDisplayMode` (`Category`) | Bone list grouping: `Hierarchy` or `Category` |
| `SkeletonViewMode` | `SkeletonViewMode` (`Default`) | Overlay style: `Default` (dots+lines), `Octahedra` (Blender-style), `Joints` |
| `ShowSelectedBonesOnly` | `bool` (`false`) | Overlay filter |
| `SymmetryMode` | `SymmetryMode` (`Off`) | `Off`, `Copy` (same transform to `_l`/`_r` pair), `Mirror` (mirrored transform) |

## Events

None published, none consumed. Changes are not observable — consumers poll per frame.

## Dependencies

None (pure state).

## Brio counterpart

`Brio/Brio/Game/Posing/PosingService.cs` — Brio keeps gizmo state (`Operation`, `CoordinateMode`, `GizmoStaysWhenAllBonesAreDisabled`) on its PosingService, and **persists** `Operation` through `Configuration.Posing.LastGizmoOperation`.

Differences: Poser separates tool state from the posing engine entirely (its `PosingService` in `Game/` does actor transforms only). Poser adds `SkeletonViewMode`, `BoneDisplayMode`, `ShowSelectedBonesOnly`, `DebugMode`, and `SymmetryMode` here; Brio's equivalents of overlay filtering live in `BoneFilter`/overlay config. Brio's `PosingCoordinateMode`/`PosingOperation` map 1:1 to `TransformOrientation`/`TransformTool`.

## Known risks

- **Nothing is persisted** — every GPose session starts back at `Rotate`/`Local`/`Category`. Brio persists at least the gizmo operation; if that behavior is wanted, these belong in `PoserConfiguration` instead.
- No change notification: anything that wants to react to a tool switch (e.g. re-render an overlay cache) has to diff values per frame.
- Singleton mutable state shared by all windows; two UI surfaces writing different values will silently fight.

## Test coverage

- **Live acceptance**: UI dispatch scenarios verify defaults and every editor
  state transition through the controls that consume it.
- **In-game only**: nothing.
