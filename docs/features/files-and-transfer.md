# Pose files and transfer

`.pose` is PascalCase pretty-printed JSON — a subset of Brio v3 so files
interchange with Brio and (via name conversion) Anamnesis.

- `Bones` values are **absolute model-space snapshots** (`LastRawTransform`)
  from all character partials. `MainHand`/`OffHand`/`Prop`/`Ornament` exist
  in the schema for Brio compatibility, but Poser exports/applies only the
  character `Bones` — slot discovery/application is deferred work. Numerics
  serialize as comma-space strings via the custom converters (the
  Brio/Anamnesis wire format — without them structs write as `{}`). Unknown
  members are ignored both ways, so Brio v3 files load fine. Poser writes no
  format version; adopt `FileVersion` first if diverging.
- `ModelDifference` applies only with `ApplyModelTransform` (default false,
  Brio parity). Anamnesis names rewrite through the 161-entry Brio table.
- `.cmp` carries no positions — import forces `ApplyPosition = false` so a
  `.cmp` can never zero a pose.
- Hazard: pre-2026-07-15 Poser exports stored deltas in `Bones` and import
  incorrectly; they are indistinguishable from absolute files.
- Import UI: one Scope dropdown (Full/Body/Expression/Selected) plus
  component, descendant, and reset-before-import options. Selected feeds
  canonical names into `BoneFilter`. The Expression preset applies face
  bones with `j_kao` excluded; face reconcile skips when any bone has IK.
- File import is not undoable (known gap). In-memory copy/stash uses
  `PortablePose` and is fully history-integrated.
