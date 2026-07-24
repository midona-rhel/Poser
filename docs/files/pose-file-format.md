# .pose File Format

As read/written by `PosingCore/Files/PoseFile.cs` (+ `AnamnesisBoneNameConverter` in the same file, `PoseImportOptions.cs` for import filtering, `PoseFileService.cs` for apply/capture — see `docs/services/pose-file-service.md`).

## Overview

`.pose` is a JSON document, **PascalCase** property names, pretty-printed. Poser's schema is a subset of Brio's v3 format so that files interchange with Brio, and (via bone-name conversion) with Anamnesis-era files.

## Schema as PosingCore reads/writes it

```jsonc
{
  "TypeName": "Brio Pose",          // string, default "Brio Pose"

  // Metadata — all optional (null omitted on write)
  "Author": "…",
  "Description": "…",
  "Version": "…",                    // free-form string, NOT Brio's FileVersion int
  "Base64Image": "…",                // preview thumbnail, used by the library grid
  "Tags": ["…"],

  // Model transform (read, currently never applied — see risks)
  "ModelDifference":     { "Position": {...}, "Rotation": {...}, "Scale": {...} },
  "ModelAbsoluteValues": { "Position": {...}, "Rotation": {...}, "Scale": {...} },

  // Bone dictionaries: game bone name → BoneData
  "Bones":    { "j_kosi": { "Position": {"X":0,"Y":0,"Z":0},
                            "Rotation": {"X":0,"Y":0,"Z":0,"W":1},
                            "Scale":    {"X":1,"Y":1,"Z":1} }, ... },
  "MainHand": { ... },
  "OffHand":  { ... },
  "Prop":     { ... },               // parsed, ignored on import
  "Ornament": { ... },               // parsed, ignored on import

  // Legacy root-level actor transform (other pose tools)
  "Position": {...}, "Rotation": {...}, "Scale": {...}
}
```

`BoneData` = `Position: Vector3`, `Rotation: Quaternion`, `Scale: Vector3`, with implicit conversions to/from `Poser.Entities.Transform`. `BoneData.Identity` is `Position = 0, Rotation = identity, Scale = 0` (Scale **zero**, mirroring Brio's `Transform.Identity` used for `ModelDifference` defaults — do not use it as a neutral bone scale).

### Serialization rules (`PoseFile.JsonOptions`)

- `System.Text.Json`, `WriteIndented = true`, no naming policy (PascalCase preserved to match Brio), `AllowTrailingCommas`, `UnsafeRelaxedJsonEscaping` — mirroring `Brio/Brio/Core/JsonSerializer.cs`.
- Numerics serialize as comma-space **strings** (`"Position": "0.25, 1, -0.5"`) via the converters in `PosingCore/Files/Converters/JsonNumericsConverters.cs` — the Brio/Anamnesis wire format. Without them STJ writes field-based structs as `{}` (bug fixed 2026-07-15).
- Every member is written, including defaults and null metadata (`WhenWritingDefault` was removed with the converter fix to match Brio's output byte-for-byte).
- `Load`/`FromJson`/`Save` swallow all exceptions and return `null`/`false` — malformed files are indistinguishable from missing ones at this layer.

### Anamnesis compatibility

`SanitizeBoneNames()` (called by `PoseFileService.ImportPose(path)`) rewrites `Bones` keys through `AnamnesisBoneNameConverter.ToGame` — a ~30-entry map (`SpineA → j_sebo_a`, `HandLeft → j_te_l`, …) with unknown names passed through unchanged. The reverse map (`ToAnamnesis`) exists but is unused. Coverage is partial: fingers, face detail, tail, ears are not mapped, so old Anamnesis files import only their mapped subset. (Known map oddity: `EyelidLowerLeft/Right → j_f_mayu_l/r`, which are eyebrow bones.)

## Brio compatibility contract

Reference: `Brio/Brio/Files/PoseFile.cs` (`PoseData` + `PoseFile : PoseData`).

Shared and interchange-safe: `TypeName`, `ModelDifference`, `ModelAbsoluteValues`, `Bones`/`MainHand`/`OffHand`/`Prop`/`Ornament` (same `BoneData`/`Bone` shape), legacy root `Position`/`Rotation`/`Scale`, `Author`/`Description`/`Base64Image`/`Tags` (Brio inherits these from `JsonDocumentBase`).

Fields Brio v3 writes that **Poser does not have** (silently dropped if a Brio file is loaded and re-saved by Poser):

| Brio field | Type | Purpose |
|---|---|---|
| `FileVersion` | `int = 3` | Format version — Poser has no equivalent (its `Version` is a free string) |
| `ModelId` | `int` | Source model |
| `RaceSexId` | `string?` | Race/gender data path for retarget warnings |
| `FaceID` | `int?` | Face type |
| `GameVersion` | `string` | Game build the pose was captured on |

Poser ignores unknown JSON members on read (default `System.Text.Json` behavior), so Brio v3 files load fine. Brio likewise tolerates Poser files missing those members. Semantics match Brio since 2026-07-15: `Bones` values are **absolute model-space snapshots** (`LastRawTransform`) from all partials, and the model transform fields are populated on export. Files exported by Poser *before* that fix stored modification deltas for edited bones and are silently misinterpreted as absolutes — see `docs/services/pose-file-service.md`.

## Version / field notes

- Poser writes no format version at all; if the schema ever diverges from Brio's, adopt `FileVersion` first.
- `Version` (string) is metadata only, surfaced nowhere in the UI yet.
- `Prop`/`Ornament` are round-tripped but never applied (Brio applies them to companion/ornament skeletons).
- `ModelDifference` is written on export and applied additively when `PoseImportOptions.ApplyModelTransform` is `true` (default `false`, Brio parity). `ModelAbsoluteValues` and the legacy root transform are written but never read back (Brio only uses absolutes for scene loads).
- Face bones (partial 1+) are exported since 2026-07-15; pre-fix Poser exports have a default face when applied in Brio.

## Test coverage

- **Live acceptance**: pose-file scenarios cover serialization round trips,
  real Brio exports, Anamnesis name conversion, malformed-input rejection, and
  capture/apply semantics on a controlled actor.
- **In-game only**: nothing for the format itself (apply behavior is `PoseFileService`/`BonePosingService` territory).


## CMTool (.cmp) support (2026-07-18)
`Files/CMToolPoseFile.cs` — legacy Anamnesis-era format (MIT; structure from
Brio's port). JSON of hex-string encoded values: each bone property is a
rotation quaternion (4 floats, little-endian hex bytes), `<Bone>Size` is scale.
`Upgrade()` reflects over the properties, decodes, race-gates `Hroth*`/`Viera*`
bones, and translates names via `AnamnesisBoneNameConverter` (now the
authoritative 161-entry Brio table in its own file). **`.cmp` carries no
positions** — `PoseFileService.ImportPose(path)` detects the extension and
forces `ApplyPosition = false` so a `.cmp` can never zero a pose (Brio leaves
this to the import popup).

## Expression imports & face reconcile (2026-07-18)
`PoseImportOptions.AsExpression` / `.Expression` preset: face bones only,
**j_kao excluded** — single-phase rewrite of Brio's apply-then-restore (which
needs a 4-tick resync and admits to breaking IK). After normal imports with
face application, `ReconcileFace` re-applies the j_kao subtree at its current
raw transforms (near-identity deltas are rejected, so it only acts when an
import shifted the face's basis); skipped whenever any bone has IK enabled.
