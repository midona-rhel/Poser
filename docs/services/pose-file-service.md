# PoseFileService

`PosingCore/Files/PoseFileService.cs` — interface `IPoseFileService` in `PosingCore/Services/IPoseFileService.cs`. Format types: `PoseFile`, `PoseImportOptions` in `PosingCore/Files/` (schema documented in `docs/files/pose-file-format.md`).

## Purpose

Imports and exports Brio-compatible `.pose` files against an `ISkeleton`. Export mirrors Brio's `SkeletonPosingCapability.ExportSkeletonPose`: every bone from **every partial** (body partial 0 and face/accessory partials 1+) is captured into the same `Bones` dictionary as an **absolute model-space snapshot** (`LastRawTransform`); partial roots are skipped except the skeleton root. The actor transform is exported as `ModelDifference` (vs the game-controlled transform), `ModelAbsoluteValues`, and the legacy root-level `Position`/`Rotation`/`Scale` (Brio `ModelPosingCapability.ExportModelPose` parity). Import loads JSON, converts Anamnesis bone names to game names, then applies each file bone through `IBonePosingService` (delta basis: `LastRawTransform`) filtered by `PoseImportOptions` (body/face/weapons; position/rotation/scale). The skeleton is only reset when `PoseImportOptions.ResetBeforeImport` is set (Brio's interactive import passes `reset: false`); `ApplyModelTransform` adds `ModelDifference` onto the current actor transform via `IPosingService` (Brio's non-scene path).

## Public API

| Member | Signature | Notes |
|---|---|---|
| `DefaultImportOptions` | `PoseImportOptions` | `PoseImportOptions.Default` (everything except model transform) |
| `CreatePoseFile` | `PoseFile (ISkeleton)` | In-memory capture, no file write |
| `ExportPose` | `bool (ISkeleton, string path)` | `CreatePoseFile` + `Save`; fires `OnPoseExported` |
| `ImportPose` | `bool (ISkeleton, string path, PoseImportOptions?)` | Load + `SanitizeBoneNames()` + apply |
| `ImportPose` | `bool (ISkeleton, PoseFile, PoseImportOptions?)` | Applies Bones/MainHand/OffHand + model transform per options (reset only if `ResetBeforeImport`); fires `OnPoseImported` |
| `ImportPoseWithDialog` / `ExportPoseWithDialog` | `void` | **Placeholders** — log a warning; needs UI-side dialog work (see TODO in source), zero callers today |

## Events

**Published:** plain C# events (not EventBus): `OnPoseImported(ISkeleton)`, `OnPoseExported(ISkeleton, string path)`.

**Consumed:** none.

## Dependencies

`IPluginLog` (Dalamud), `IBonePosingService` (`ResetSkeleton`, `ApplyTransform`), `IPosingService` (`GetEffectiveTransform`, `GetOriginalTransform`, `SetTransformOverride`, `ClearTransformOverride` — actor/model transform for export metadata and `ApplyModelTransform` import).

## Brio counterpart

`Brio/Brio/Game/Posing/PoseImporter.cs` (+ `PoseImporterOptions`), applied via `PosingCapability.ImportPose/ExportPose`; file type integration in `Brio/Brio/Files/PoseFile.cs` (`PoseFileInfo`).

Differences:
- Brio filters import by a **`BoneFilter`** (category-driven allow-list from `BoneCategories`) and `TransformComponents` flags; Poser uses coarse booleans plus a **name-prefix heuristic** for "face bone".
- Brio has a two-phase **expression** import mode (`expressionPhase` handles `j_kao` specially); Poser's `RotationOnly` preset approximates the use case with no special-casing.
- Brio import respects `PoseInfoSlot` (Character/MainHand/OffHand are distinct skeletons); Poser looks MainHand/OffHand bone names up **on the same skeleton**.
- Brio records pose metadata on export (`ModelId`, `RaceSexId`, `FaceID`, `GameVersion`, `FileVersion = 3`); Poser writes bones + model transform (`ModelDifference`/`ModelAbsoluteValues`/legacy root transform) but none of the v3 identity metadata.
- Brio pushes imports through the history snapshot system; Poser's import is **not undoable** (no history action is recorded).

## Known risks

Fixed 2026-07-15 (kept for history; covered by the live pose-file scenarios):

- ~~Face partials dropped on export~~ — export now walks all partials into `Bones` (Brio parity), skipping partial roots except the skeleton root.
- ~~Mixed delta/absolute semantics in `Bones`~~ — export writes absolute `LastRawTransform` snapshots consistently; import applies them against a `LastRawTransform` basis. Poser exports now satisfy the Brio round-trip contract (needs in-game cross-tool re-verification, P5).
- ~~`ApplyModelTransform` never honored~~ — import now applies `ModelDifference` additively to the actor transform via `IPosingService`; export writes `ModelDifference`/`ModelAbsoluteValues`/legacy root transform.
- ~~Unconditional `ResetSkeleton` on import~~ — reset is now opt-in via `PoseImportOptions.ResetBeforeImport` (default false, matching Brio's interactive import); when set together with `ApplyModelTransform` the actor override is also cleared first (Brio's `applyModelTransform && reset` behavior).

Still open:

- `Prop` and `Ornament` dictionaries in the file are ignored entirely.
- `ModelAbsoluteValues` is written but never used on import (Brio only uses it for scene loads in "Absolute" mode; Poser has no scene import).
- `IsFaceBone` heuristic misclassifies: `j_kami` (hair) and `j_mimi` (ears) are treated as face; conversely IVCS/face partial bones not matching the prefixes slip through the `ApplyFace=false` filter.
- `ApplyFace` only filters **within** `ApplyBody` — `ApplyBody=false` skips face bones too, even with `ApplyFace=true` (Brio's BoneFilter categories are independent).
- Old Poser-exported files (pre-fix, delta-valued `Bones`) are indistinguishable from absolute-valued files and will import incorrectly.
- Dialog methods are stubs with zero callers; the UI opens its own `FileBrowser` and passes paths. Wiring them needs UI-side work (see `TODO(UI)` in `PoseFileService.cs`).
- Import is still not undoable (no history action recorded).

## Test coverage

- **Live acceptance**: pose-file scenarios cover JSON serialization, export
  across all partials, partial-root skipping, absolute `LastRawTransform`
  values, model-transform fields, reset semantics, component/face filters,
  round trips, visible Havok deformation, weapon skeletons, and the Brio
  cross-tool contract (P5 in `docs/process/in-game-verification.md`).
