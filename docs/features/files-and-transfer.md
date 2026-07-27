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
  component, descendant, and reset-before-import options. Weapon, prop,
  and ornament application are internal import options selected by the
  Full/Selected scopes, not individual controls. Full includes every
  slot; Body and Expression are Character-only; Selected uses the
  selected bones' exact slots. Reset-before-import touches only the
  chosen scope; the model transform applies once to the owning actor.
  The Expression preset applies face bones with `j_kao` excluded; `.cmp`
  remains Character-only.
- File import is ONE atomic undoable edit: the importer computes a plan
  without mutating, every affected exact slot-qualified target (including
  reset-before-import and the model transform) is captured first, a
  failure restores everything and appends no history item, and success
  appends exactly one. The Selected-scope filter freezes at dialog
  confirmation; the target actor freezes at dialog open. In-memory
  copy/stash uses `PortablePose` and is equally history-integrated.

## Character files (MCDF)

- MCDF v1 (Mare/Brio/Ktisis interchange) is a legacy K4os LZ4 stream:
  ASCII `MCDF`, version byte 1, little-endian int32 JSON length, UTF-8
  JSON (`Description`, `GlamourerData`, `CustomizePlusData`,
  `ManipulationData`, `Files[{GamePaths,Length,Hash}]`,
  `FileSwaps[{GamePaths,FileSwapPath}]`), then raw payloads in `Files`
  order. Unknown members are ignored; unknown versions fail. MCDF carries
  appearance resources ONLY — never pose, animation, selection, camera,
  or scene data; `.pose` remains the only pose format.
- Import validates before any actor change: magic/version, complete
  reads, config-backed hard limits (total/one-file bytes, entry and path
  counts), normalized relative lower-case game paths, the Brio extension
  allow-list, byte-identical-only duplicates, game-path-to-game-path
  swaps, and SHA-1 payload verification. Extraction uses generated names
  in a unique temp operation directory. A successfully imported MCDF
  RETAINS its extracted payloads while owned — the live temporary
  collection references them — and they are deleted on Reset MCDF,
  rollback, teardown, GPose exit, and disposal, only once that
  collection is definitely gone; a failed deletion stays owned and
  retryable.
- One import/export runs at a time with an immutable progress snapshot
  and cooperative cancellation. Import applies as a transaction
  (temporary collection → temporary mods/manipulations → locked Glamourer
  state → bounded redraw wait with binding refresh → temporary body
  profile) and commits ownership only when complete; failure or
  cancellation rolls back in reverse order, and a partial rollback stays
  owned and retryable through **Reset MCDF**. Re-import tears the active
  MCDF down first. Export is read-only, refuses an MCDF-wearing actor
  and foreign Glamourer locks, keeps swaps as swaps, applies Brio's
  compatibility filter, deduplicates payloads by SHA-1, reports every
  skipped resource by name, and replaces the destination atomically via
  a `.tmp`. Ownership/baseline semantics live in
  [runtime-appearance.md](runtime-appearance.md).
