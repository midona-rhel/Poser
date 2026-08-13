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
  format version; adopt `FileVersion` first if diverging. Reads are bounded to
  32 MiB and JSON depth 64; each slot collection is bounded to 8,192 entries,
  all five to 32,768, and bone names/tags to 256 characters (256 tags). Used
  numerics must be finite and rotations nondegenerate. Quaternion normalization
  occurs only when materializing a plan; `ModelDifference.Scale` is additive,
  so zero is valid. Anamnesis aliases that converge on one game name are a
  deterministic conflict, never last-write-wins.
- Saves validate and serialize completely before touching the destination,
  write and durably flush a unique same-directory temp, reopen and validate it,
  then replace/move atomically. Failure preserves the previous destination;
  an undeletable temp is returned as recovery evidence. Legacy nullable/bool
  codec methods are intentionally lossy compatibility wrappers over typed
  `.pose` outcomes.
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
  semantics) is synchronously detached with
  `IPoseFileService.CreatePoseFile`; the owned worker then serializes and writes
  that immutable data into
  `<pluginConfigDir>/AutoSaves/<yyyy-MM-dd>/<HH-mm-ss> <actor>.pose`
  (one folder per LOCAL day — user call 2026-08-08, replacing the
  references' folder-per-save clutter; 24-hour prefix keeps name order ==
  time order within a day; names sanitized, same-snapshot duplicates and
  same-second collisions suffixed ` (2)`). No actor qualifies → no
  folder. First save lands one full interval after entering GPose, then
  every interval.
- Lifecycle ownership and exit ordering are defined once in
  [application-state.md](../architecture/application-state.md). Its successful
  framework-dispatch qualification, applicable final-capture versus
  clean-on-exit behavior, and dispatch-failure/provider-disposal fallback
  apply here; this file does not restate those lifecycle phases. A crash
  leaves snapshots for recovery.
- Capture produces an immutable snapshot. The persistence worker receives only
  that snapshot, serializes/writes it, and is never detached as best-effort
  work. Applicability, join ordering, and dispatch-failure semantics are
  defined in [application-state.md](../architecture/application-state.md). A
  final snapshot is not successful merely because capture occurred: capture or
  persistence failure, and final worker join/drain failure, produce typed
  failure or `RecoveryRequired` evidence. Lifecycle ownership and receipt
  semantics are defined once in
  [application-state.md](../architecture/application-state.md).
- Retention prunes from DISK to the configured count of SAVE EVENTS —
  the files sharing one time prefix in a day folder, or one whole folder
  of the pre-2026-08-08 folder-per-save layout, which ages out through
  the same ordering with no migration. Newest-first by write DATE
  (Brio's semantic, since a save is written once; key breaks ties, so a
  renamed folder or file keeps its true age — floor 1), so it holds
  across restarts; a day folder whose last event is pruned goes with it.
  Every IO failure
  logs an Error with the path and never aborts the remaining
  actors/folders. Recovery: the titlebar burger menu → "Auto-saves…"
  (enabled when the selected actor has a skeleton; the ONE entry point)
  opens the import browser rooted at the auto-save directory; a
  recovered file flows through the standard import pipeline. Settings
  (General → AUTO-SAVE): enabled, interval 10–600 s, kept count (free
  numeric input, floor 1, no cap), clean-on-exit — read live each tick.

The storage boundary is intentionally narrow. Versioned codecs, finite-value
validation, same-directory atomic replacement, autosave queue/join, library
index, and quarantine/recovery records belong to host-free `Poser.Persistence`
only if that assembly can reference `Domain` and the minimum Application
storage contracts. It must never reference Game, Dalamud, ImGui, native state,
or live UI state; otherwise the implementation remains behind the same ports
in Application. Native materialization stays in Game and logical transaction,
identity, and receipt semantics stay in Application.

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
