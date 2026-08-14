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
  Brio/Anamnesis wire format — without them structs write as `{}`). Root
  members Poser does not model are never interpreted but ARE carried
  (`PoseFile.UnmappedMembers`), so a Brio v3 file both loads and survives a
  rewrite with everything Brio consumes intact; nothing in Poser reads or
  writes that carry, so it can never shadow a named member, and it is only
  ever populated by a read the bounds below already passed. Poser writes no
  format version; adopt `FileVersion` first if diverging. Reads are bounded to
  32 MiB and JSON depth 64; each slot collection is bounded to 8,192 entries,
  all five to 32,768, and bone names/tags to 256 characters (256 tags). Used
  numerics must be finite and rotations nondegenerate. Quaternion normalization
  occurs only when materializing a plan; `ModelDifference.Scale` is additive,
  so zero is valid. Anamnesis aliases that converge on one game name are a
  deterministic conflict, never last-write-wins.
- Saves validate and serialize completely before touching the destination,
  write and durably flush a unique same-directory temp, reopen and validate it,
  then replace/move atomically. Existing files use a unique same-directory
  backup until the committed bytes are confirmed; an uncertain commit never
  deletes the sole old or validated-new copy, and every surviving temp/backup
  is returned as recovery evidence. Cleanup uses unconditional delete (missing
  is success) only after the relevant postcondition. Legacy nullable/bool codec
  methods are intentionally lossy wrappers over observation-only typed `.pose`
  outcomes.
- The store is synchronous and stateless. A caller must not mutate a `PoseFile`
  while writing it; concurrent callers get last-successful-writer filesystem
  semantics. Destination and parent paths are trusted, with operating-system
  reparse-point and race behavior; this boundary makes no path-containment
  security claim.
- `ModelDifference` applies only with `ApplyModelTransform` (default false,
  Brio parity). Anamnesis names rewrite through the 161-entry Brio table.
- `.cmp` carries no positions — import forces `ApplyPosition = false` so a
  `.cmp` can never zero a pose.
- Hazard: pre-2026-07-15 Poser exports stored deltas in `Bones` and import
  incorrectly; they are indistinguishable from absolute files.
- Import UI (Brio popup parity): Body and Expression type checkboxes,
  per-component options, Reset-first, and a bone-filter popup that shapes
  only the default path (disabled while either type is checked).
  Selected-bones and Include-descendants rows mount only in the import
  dialog; confirm freezes the live bone selection into exact `BoneId`s.
  Directly selected bones bypass the type strip, category exclusions,
  face gate, and slot enables — those gates apply only to descendant
  expansion (Ktisis ApplyToBones parity) — and the reset scope mirrors
  the bypass. A-pose/T-pose presets and a two-step-armed Reference
  preset (arm with a visible warning, disarm on menu reopen) live with
  the import surfaces. Weapon, prop, and ornament application are
  internal import options, not individual controls: a default import
  (neither type checked) includes every slot; Body and Expression are
  Character-only; selected bones use their exact slots.
  Reset-before-import touches only the chosen scope; the model transform
  applies once to the owning actor. Expression applies face bones with
  `j_kao` excluded (the carve survives even the direct-bone bypass —
  engine head-restore mechanics, not a gate); `.cmp` remains
  Character-only.
- File import is ONE atomic undoable edit: the importer computes a plan
  without mutating, every affected exact slot-qualified target (including
  reset-before-import and the model transform) is captured first, a
  failure restores everything and appends no history item, and success
  appends exactly one. The Selected-scope filter freezes at dialog
  confirmation; the target actor freezes at dialog open. In-memory
  copy/stash uses `PortablePose` and is equally history-integrated.

## Pose library indexing

- Library scans run off-thread and publish one immutable snapshot only after a
  complete bounded pass. Directory depth, folder count, file count, file size,
  and JSON depth are bounded by the shared codec/traversal limits; cancellation,
  traversal failure, or a bound breach retains the previous snapshot.
- `.pose` metadata is observed through the typed pose codec. Each entry carries
  `Valid`, `Corrupt`, `Future`, or `Oversized` status with a concise detail —
  one classification (`PoseLibraryFileActions.Classify`) shared by the scan
  and the retry probe. Flagged entries stay VISIBLE: the tile carries a
  warning badge and the info strip states the typed diagnosis. A
  `.poserscene` is a different document read by its own codec and classified
  by the matching `Classify` overload — the scan and the probe BOTH dispatch
  on extension, because a scene re-read with the pose codec would answer
  Corrupt however healthy it is.
- Search matches the entry's name, author, and tags (substring, against runs
  lowercased at scan time); the tag chip remains an exact filter. `Author` is
  always the document's own author member — a scene whose document names
  nobody carries no author rather than borrowing its description, so no entry
  answers an author search with prose it did not author. Folders are
  the on-disk tree, bounded by the traversal limits, with per-kind recursive
  counts so a tab drops empty subtrees whole.
- File verbs live in `PoseLibraryFileActions` — synchronous, stateless, every
  outcome typed (`PoseLibraryFileActionResult`), and none of them mutates the
  published snapshot: a successful mutation requests a fresh complete pass.
  - Recovery (flagged entries): **Retry** re-probes through the same bounded
    metadata seam; **Quarantine** moves the file into its directory's
    `.quarantine` folder (collision-suffixed, never overwriting earlier
    evidence) which the scan skips by name; **Restore** moves it back
    (collision-suffixed, never overwriting a live file); **Reveal**/**Delete**
    round it out. Delete is confirm-gated in UI and idempotent at the core.
  - Authoring: **Rename** (same-directory, extension kept, taken names
    refused), **Move** to another scanned folder (missing destination or
    taken name refused), **Edit metadata** — author and tags written back
    into the file through the atomic store's bounded read + validate +
    same-directory atomic replace; tags are trimmed, deduplicated
    case-insensitively, and bounded by the codec's 256-tag limit. Brio's
    `SaveMetadata` edits five members (author, version, description, tags,
    thumbnail); Poser's edits two, and reaches Brio's FIDELITY rather than its
    field set — Brio edits through its full document, Poser preserves the
    unmodelled root members it carries. Because this is Poser's only rewrite
    of a file it did not author, eligibility is `Valid` ONLY: an unreadable
    file is refused by the read, and a `Future` one is refused explicitly,
    since a schema Poser has already said it does not support must not be
    rewritten. Refusals leave the file byte-identical.
- Favourites key on the absolute path; a path-changing verb carries the
  favourite along, and delete/quarantine drop it.

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
- `AutoSaves/.autosave-health.json` is the root-level atomic health read model.
  It records bounded operation identity, reason, intended/written counts,
  affected paths, terminal status, and failure/recovery evidence; structured
  recovery entries retain at most four entries and report any discarded entries
  in an explicit overflow count. Pending,
  queued, or dispatch-accepted records found at startup are promoted to
  `RecoveryRequired` with an `Interrupted` phase; failure to update the record
  is itself a recovery failure, blocks new admissions, and never implies durable
  success. Existing terminal observations (`Written`, `Cleaned`,
  `RecoveryRequired`, or `Cancelled`) are preserved as evidence without a
  promotion attempt and do not block new admissions. The health file is not a
  snapshot folder and is excluded from retention enumeration.
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
- Every pose a snapshot writes records WHERE it was taken — the territory id
  and the place name resolved at capture, through the ONE resolution whole-scene
  capture uses ([scenes.md](scenes.md)). Both members are optional and are
  omitted when unset, so an ordinary export's bytes are unchanged and a snapshot
  written before this shipped carries neither. The library's auto-save tab is
  filed by a left RAIL, one row per day AND place ("2026-08-14 – Limsa
  Lominsa"); selecting a row filters the grid, which keeps tiles only. A file
  recording no place gathers under its day alone — no place is ever inferred.
- The library's auto-save tab's footer is a REFUSAL channel, not a health
  readout: it speaks only for a `RecoveryRequired` terminal result or health
  record, and says nothing otherwise. A working cadence is not news, and
  narrating it (a last-accepted-save stamp, an off state, an empty session)
  gave that one tab a bar of chrome no other library tab carries. Every
  library tab lays out the same four bands and the same single action row —
  no tab carries component toggles of its own, because the inspector owns
  which of position/rotation/scale a pose applies and an auto-save restore is
  full-fidelity by contract.
- The WHOLE SCENE has its own snapshot on the same cadence and its own root;
  see [scenes.md](scenes.md).

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
  collection is definitely gone AND, on a still-resolvable actor whose
  temporary Penumbra ownership was removed, a bounded exact-actor
  redraw-complete barrier has passed (the current draw object may read
  the extracted files until it rebuilds). A failed barrier or deletion
  keeps the directory owned as retryable evidence for Reset MCDF; an
  unresolvable actor has no draw object and releases immediately.
- One import/export/teardown-barrier transaction runs at a time, owned
  by `McdfTransaction` behind the `ActorIntegrationSession` facade. Each
  operation carries the exact actor generation, the active
  `SessionGeneration`, an owner-local `OperationEpoch`, and an operation
  id, and publishes an immutable progress snapshot plus one
  `OperationReceipt` (Pending → Applied/Failed/Cancelled — the contract
  in [application-state.md](../architecture/application-state.md)).
  Every framework phase re-guards on invalidation, cooperative
  cancellation, and the exact session token before mutating, and
  terminal publication is refused for anything but the current
  operation — a late completion can neither mutate a replacement nor
  overwrite a newer terminal. Import applies as a transaction
  (temporary collection → temporary mods/manipulations → locked Glamourer
  state → bounded redraw wait with binding refresh → temporary body
  profile) and commits ownership only when complete; failure or
  cancellation invalidates FIRST, then rolls back in reverse order, and
  a partial rollback stays owned and retryable through **Reset MCDF**.
  Re-import tears the active MCDF down (including its barrier-gated
  directory release) before applying anything. Disposal cancels and
  joins the active MCDF task within a bound and THEN tears down committed
  ownership, both before the integration port is disposed. Every surface
  that STARTS an import also carries its stop while the operation is
  pending — the appearance pane's progress row and the library's MCDF
  tab — because the transaction is single-flight and long.
  Export is read-only, refuses an MCDF-wearing actor
  and foreign Glamourer locks, keeps swaps as swaps, applies Brio's
  compatibility filter, deduplicates payloads by SHA-1, reports every
  skipped resource by name, and replaces the destination atomically via
  a `.tmp`. Ownership/baseline semantics live in
  [runtime-appearance.md](runtime-appearance.md).
