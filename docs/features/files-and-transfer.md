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

## Auto-save

- While in GPose, every actor passing the authored-edits predicate (any
  bone stack with a null layer — `CleanPoseFacade.HasAuthoredEdits`
  semantics) exports through the normal `IPoseFileService.ExportPose`
  path — auto-saves are byte-model-identical to manual exports — into
  `<pluginConfigDir>/AutoSaves/<yyyy-MM-dd HH-mm-ssZ>/<actor>.pose`
  (UTC, 24-hour, name order == time order; names sanitized, duplicates
  suffixed ` (2)`). No actor qualifies → no folder. First save lands one
  full interval after entering GPose, then every interval.
- GPose exit takes one final snapshot while the pose is still intact —
  the auto-save handler MUST stay first in `GPoseStateChangedEvent`
  subscription order (eager-resolve order in `Poser.cs`; the scene
  services are injected as factories to keep it so). Disconnect and
  posing-disable both surface as this same edge. With clean-on-exit the
  exit instead deletes all snapshots — a crash never runs it, so
  snapshots survive for recovery.
- Retention prunes from DISK to the configured count (newest-first by
  folder DATE — `Directory.GetLastWriteTimeUtc`, Brio's semantic, since a
  snapshot folder is written once; name breaks ties, so a renamed folder
  keeps its true age — floor 1), so it holds across restarts. Every IO failure
  logs an Error with the path and never aborts the remaining
  actors/folders. Recovery: the titlebar burger menu → "Auto-saves…"
  (enabled when the selected actor has a skeleton; the ONE entry point)
  opens the import browser rooted at the auto-save directory; a
  recovered file flows through the standard import pipeline. Settings
  (General → AUTO-SAVE): enabled, interval 10–600 s, kept count (free
  numeric input, floor 1, no cap), clean-on-exit — read live each tick.

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
