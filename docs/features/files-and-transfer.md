# Pose files and transfer

`.pose` is PascalCase pretty-printed JSON — a subset of Brio v3 so files
interchange with Brio and (via name conversion) Anamnesis.

- Collections map slots exactly — Character→`Bones`, MainHand→`MainHand`,
  OffHand→`OffHand`, Prop→`Prop`, Ornament→`Ornament` — as **absolute
  model-space snapshots** (`LastRawTransform`, non-root partial roots
  skipped). Each collection imports only into its matching live slot;
  unavailable slots are skipped and reported, never redirected by bone
  name to Character. Numerics
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
  component, weapon/prop/ornament, descendant, and reset-before-import
  options. Full includes every slot; Body and Expression are
  Character-only; Selected uses the selected bones' exact slots.
  Reset-before-import touches only the chosen scope; the model transform
  applies once to the owning actor. The Expression preset applies face
  bones with `j_kao` excluded; face reconcile and `.cmp` remain
  Character-only.
- File import is not undoable (known gap). In-memory copy/stash uses
  `PortablePose` and is fully history-integrated.
