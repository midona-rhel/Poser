# PBI-009 — Actor files and external appearance workflows

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Extra large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-009-base` |
| Feature branch | `feature/pbi-009-actor-files-integrations` |
| Accepted head | Not accepted |

## Outcome

Complete the file and external-appearance workflow for ONE selected actor.
From Poser's retained main window, the user can:

- import, export, and undo actor pose files through the existing pose system;
- choose and restore a Penumbra collection, Glamourer design, or Customize+
  profile for the exact actor;
- import an `.mcdf` package onto that actor, inspect progress, cancel safely,
  and restore everything the import replaced;
- export that actor's current mod resources, manipulations, Glamourer state,
  and Customize+ scale as a compatible `.mcdf`.

This PBI adds no file library and no whole-scene save/load. It is an
actor-scoped workflow, not a project system or a second appearance editor.

Brio is the behavioral reference for MCDF export and complete appearance
capture. Ktisis is the interaction/reference implementation for applying and
reverting MCDF on a chosen actor. Current Penumbra, Glamourer, and Customize+
APIs are authoritative over copied legacy call signatures.

## Product boundary

Retain the current main window, sidebar, Pose tab, Appearance tab, and
inspector. Add no standalone browser, library window, scene format, scene
catalog, thumbnails, tags, favourites, cloud sharing, or autosave.

MCDF contains appearance resources only. It does NOT contain Poser's pose,
animation playback state, selection, camera, environment, lights, spawned
actor list, or scene layout. `.pose` remains the only pose format.

Do not add gear/customization editing controls that compete with Glamourer.
The selectors invoke the owning plugins and retain only the identifiers and
restore data necessary for this GPose session.

Brio and Ktisis are GPL reference projects while Poser is MIT. Reimplement
the documented wire format and behavior independently; do not copy their
source, comments, names, or implementation structure into Poser.

## Reference baseline and deliberate decisions

Reference revisions are pinned so later upstream changes do not silently
rewrite this contract:

- Brio `73bb59d`: [MCDF service](https://github.com/Etheirys/Brio/blob/73bb59d2c653a5ee21f1ac0dbe28ade16d5b803e/Brio/MCDF/Game/Services/MCDFService.cs),
  [actor appearance ownership](https://github.com/Etheirys/Brio/blob/73bb59d2c653a5ee21f1ac0dbe28ade16d5b803e/Brio/Capabilities/Actor/ActorAppearanceCapability.cs),
  [MCDF teardown](https://github.com/Etheirys/Brio/blob/73bb59d2c653a5ee21f1ac0dbe28ade16d5b803e/Brio/Game/Actor/CharacterHandlerService.cs),
  and [external selectors](https://github.com/Etheirys/Brio/blob/73bb59d2c653a5ee21f1ac0dbe28ade16d5b803e/Brio/UI/Controls/Editors/AppearanceEditorCommon.cs).
- Ktisis `a5ae200`: [MCDF reader](https://github.com/ktisis-tools/Ktisis/blob/a5ae200d92c7b7a830f6c171c521ff3aed829e53/Ktisis/Data/Mcdf/McdfReader.cs),
  [actor apply/revert manager](https://github.com/ktisis-tools/Ktisis/blob/a5ae200d92c7b7a830f6c171c521ff3aed829e53/Ktisis/Data/Mcdf/McdfManager.cs),
  and [actor context action](https://github.com/ktisis-tools/Ktisis/blob/a5ae200d92c7b7a830f6c171c521ff3aed829e53/Ktisis/Interface/Editor/Context/SceneEntityMenuBuilder.cs).
- Glamourer `416f474`: [design and actor-state API](https://github.com/Ottermandias/Glamourer/tree/416f474388060a2e9509b36b4c6b7f4cb0dd46da/Glamourer/Api).
- Penumbra.Api `874a377`: [collection API](https://github.com/Ottermandias/Penumbra.Api/blob/874a3773bc4f637de1ef1fa8756b4debe3d8f68b/Api/Collection.cs),
  [temporary-resource API](https://github.com/Ottermandias/Penumbra.Api/blob/874a3773bc4f637de1ef1fa8756b4debe3d8f68b/IpcSubscribers/Temporary.cs),
  and [actor resource paths](https://github.com/Ottermandias/Penumbra.Api/blob/874a3773bc4f637de1ef1fa8756b4debe3d8f68b/IpcSubscribers/ResourceTree.cs).
- Customize+ `0f3dfba`: [profile IPC contract](https://github.com/Aether-Tools/CustomizePlus/blob/0f3dfba34c0bdd60f2d098de7ac3e3253a84fc04/CustomizePlus/Api/CustomizePlusIpc.Profile.cs).
- Character Select+ `99e9809`: [public IPC provider](https://github.com/IcarusXIV/Character-Select-/blob/99e98095409b66b3286c32635b8b32d77202fd71/CharacterSelectPlugin/IPCProvider.cs).

Deliberate compatibility decisions:

- Brio can import AND export MCDF. Ktisis imports and reverts but has no MCDF
  writer. Poser therefore follows Ktisis' actor targeting and Brio's export
  coverage, with stricter rollback and cancellation than either reference.
- Use current `GetGameObjectResourcePaths.V5` and actor-specific
  `GetMetaManipulations.V5` for export. Do not port Brio's old transient
  resource-recording hook unless a live export proves the current Penumbra
  resource tree omits required files.
- Use `K4os.Compression.LZ4.Legacy` `1.3.8` only for the MCDF legacy stream.
  It is the one allowed new package; do not add a general archive framework.
- Character Select+ exposes character/design LIST and SWITCH calls, but its
  switch has no object index and applies its complete workflow to the local
  player. It also exposes no matching restore call. Do not scrape its config,
  fake a target, or present it as actor-scoped. A disabled explanatory row or
  Open Quick Switch handoff is acceptable; arbitrary-actor application is
  deferred until Character Select+ provides target and restore IPC.

## Exact actor boundary

Every command begins from the current stable selection:

- an actor selection targets that `ActorId`;
- a bone selection targets its owning `ActorId`;
- no selection, a stale generation, a non-character actor, or a missing draw
  object fails visibly before mutation;
- the target object index/address is resolved only at the runtime call
  boundary and is never retained by UI or Application code.

Capture the exact `ActorId` when a file dialog or picker action begins. A
selection change while a dialog is open does not retarget the pending action.
Before commit, revalidate the captured generation. Never redirect to the
current game target, local player, another actor with the same name, or a
replacement generation.

MCDF import is permitted only for a supported GPose actor and never for the
live overworld player. Export is permitted for any supported selected GPose
actor with a valid draw object. State why an actor is unavailable through
`HoverHelp` and the page status line.

## One integration session and ownership

Add one stable-id `ActorIntegrationSession` in Application with a narrow
runtime port implemented in Game. UI owns no IPC subscriber, object index,
file task, cancellation source, extracted path, or restore snapshot.

The first successful external appearance change captures the actor's incoming:

- Penumbra effective collection GUID AND whether an individual assignment
  existed (restoring an inherited collection means deleting Poser's
  individual assignment, not assigning the effective collection permanently);
- complete Glamourer actor state through its supported serialized-state API;
- active Customize+ saved-profile identity when it is readable.

The current Customize+ API returns an active profile id but `GetByUniqueId`
does not return another plugin's temporary profile data. If an actor already
has an unreadable temporary profile, refuse any C+ or MCDF operation that
would displace it. Do not delete it and pretend it can be restored.

Direct Collection, Design, and C+ selectors may coexist. MCDF is an exclusive
bundle over all three: importing MCDF replaces any current Poser-authored
external recipe but keeps the original captured baseline. While MCDF owns the
actor, direct selectors are disabled until **Reset MCDF**.

Never unlock or overwrite a Glamourer state locked by another plugin. If the
incoming state cannot be captured with the caller's normal key, fail before
mutation. Do not continuously enforce ordinary selector choices.

Each runtime result is explicit. A partial reset retains ownership only for
the components still unresolved so Reset can retry. Actor generation
replacement never receives an old capture. Actor removal must still delete
Poser-created temporary resources by their own IDs/tags even if native actor
writes are no longer possible.

## External appearance selectors

Extend the existing Appearance tab with a compact **EXTERNAL APPEARANCE**
section:

- **Collection** — searchable Penumbra collection picker. The trigger shows
  the current effective collection; choosing one creates/updates only that
  actor's individual assignment and requests one redraw. Reset restores the
  prior assignment-vs-inheritance distinction.
- **Design** — searchable Glamourer design picker using
  `GetDesignList.V2`; apply with the supported actor-index design endpoint.
  Use the current API's documented default design/state flags without
  acquiring a persistent lock; do not invent compatibility flags.
  Capture the complete incoming state first. Reset reapplies that captured
  state exactly; it is not merely “revert to game”.
- **Body profile** — searchable Customize+ normal-profile picker. Retrieve
  the selected profile JSON and apply it as a temporary profile on the exact
  actor. Reset deletes ONLY Poser's temporary profile so the underlying saved
  assignment resumes naturally. An unreadable pre-existing temporary profile
  disables this action rather than being displaced.

Lists load on popover open, sort by display name, filter case-insensitively,
and cache only for that open. A plugin unload, API-version mismatch, IPC
exception, missing design/profile, or stale actor closes the operation with a
truthful result and no local “selected” lie.

Use one shared anchored searchable Popover picker, not raw ImGui combos and
not three bespoke list implementations. It shrinks to results, scrolls only
its list, shows at most ten rows before scrolling, and uses the retained glass
chrome, one-pixel item separators, optical text baselines, and HoverHelp.

## MCDF v1 wire compatibility

Implement the format independently in a small runtime/file boundary:

1. The complete file is a legacy K4os LZ4 stream.
2. Decompressed header: ASCII `MCDF`, version byte `1`, little-endian signed
   32-bit JSON byte length, then UTF-8 JSON.
3. JSON fields: `Description`, `GlamourerData`, `CustomizePlusData`,
   `ManipulationData`, `Files[{GamePaths,Length,Hash}]`, and
   `FileSwaps[{GamePaths,FileSwapPath}]`.
4. Raw file payloads follow the JSON immediately and in `Files` order.
   A file hash may map to multiple game paths; write its bytes once.

Unknown JSON members are ignored. Unknown format versions fail explicitly.
Do not silently reinterpret a malformed package.

Validate before actor mutation:

- exact magic/version, non-negative lengths, complete JSON and payload reads;
- configurable hard limits for total expanded bytes, one file, entry count,
  and path count, with conservative defaults and an explicit failure;
- normalized lower-case game paths with no rooted filesystem path or `..`;
- duplicate game paths are rejected unless byte-identical and intentional;
- file swaps must be game-path to game-path;
- only Brio-compatible resource extensions:
  `.mdl`, `.tex`, `.mtrl`, `.tmb`, `.pap`, `.avfx`, `.atex`, `.sklb`,
  `.eid`, `.phyb`, `.pbd`, `.scd`, `.skp`, `.shpk`, `.kdb`;
- each payload's computed SHA-1 matches its declared hash when a hash is
  present. Never use a hash as an unchecked filesystem path.

Extract into a unique Poser operation directory under the OS temp directory,
using generated filenames rather than archive names. Cleanup is mandatory on
success, failure, cancellation, GPose exit, and plugin disposal.

## MCDF import transaction

Only one MCDF import/export operation runs at a time. The operation exposes an
immutable progress snapshot: target actor, file name, phase, files/bytes
completed, cancellable flag, and final result.

Import phases:

1. Capture the actor id and validate/read the complete package off-thread.
2. Determine required integrations from its content. Embedded files,
   file-swaps, or manipulations require Penumbra; Glamourer/C+ are required
   only when their payload is non-empty. Missing requirements fail before any
   actor change.
3. On the framework thread, revalidate actor generation and capture the
   integration baseline. Refuse another plugin's Glamourer lock.
4. Create and assign one Penumbra temporary collection, add temporary files
   and manipulation data under a Poser-owned operation tag, apply the
   Glamourer state, and request redraw.
5. Wait for the exact actor to become drawable again with a bounded timeout,
   refresh/reconcile scene bindings, then apply the C+ temporary profile.
6. Commit ownership only after every required component succeeds.

Any failure or cancellation after phase 3 rolls back in reverse order,
restores the captured external baseline, removes every temporary mod,
collection, profile, lock, and extracted file, redraws when possible, and
reports both the original failure and any rollback failure. Cancellation is
cooperative; disposing a `Task` is not cancellation.

Importing another MCDF onto the same actor first tears down the active MCDF
transactionally. It must never stack anonymous temporary resources.

Redraw reconciliation:

- active transform gestures touching the actor cancel once with their frozen
  baseline restored and no history entry;
- actor selection survives; a selected bone that no longer exists falls back
  to its owning actor rather than another bone;
- replaced skeleton slots purge stale pose/IK/history through the existing
  exact-generation lifecycle;
- no cached draw object, skeleton, or matrix from before redraw is reused.

## MCDF export transaction

Export captures the exact selected actor at start and never retargets. Refuse
export while that actor has an imported MCDF active, matching Brio's
anti-repackaging rule. Also refuse a Glamourer state locked by another owner.

Capture through supported APIs:

- current actor resource replacements from Penumbra's object resource paths;
- actor-specific meta manipulations, not Penumbra's global/current UI
  collection;
- complete Glamourer actor state as its supported base64 payload;
- active Customize+ profile JSON, base64-encoded for MCDF compatibility. A
  Poser-created temporary profile is exportable from the session's retained
  JSON; another plugin's unreadable temporary profile makes export fail
  explicitly instead of silently omitting body scale.

Resolve only actual file replacements reported for the actor. Keep file swaps
as swaps. Include only existing local files under Penumbra's configured mod
root or Poser's owned cache, filter the allowed extensions above, and report
every skipped/missing resource. Do not export arbitrary filesystem paths.

Follow Brio's compatibility filter after discovery: omit `.pap`, `.tmb`, and
`.scd`; omit `.avfx`/`.atex` unless the game path belongs to weapon or
equipment. Deduplicate payloads by SHA-1 while preserving every game path.
Export never changes the actor.

Write to `<destination>.tmp`, flush and close the complete LZ4 stream, then
atomically replace/move the destination. Cancellation or failure removes the
temporary output and leaves an existing destination untouched. The final
status reports file count, uncompressed bytes, and skipped-resource summary.

## Reset semantics

Expose separate truthful actions:

- **Reset collection**, **Reset design**, and **Reset body profile** restore
  only their captured external component.
- **Reset MCDF** removes every resource and override created by the active
  MCDF and restores the complete pre-integration external baseline.
- existing **Reset appearance** continues to restore ONLY Poser's opacity,
  tint, and wetness; it does not touch external plugins or MCDF.
- existing actor **Reset All** additionally invokes Reset MCDF/external
  integrations after pose/expression/gaze/IK/animation/runtime appearance,
  aggregating failures without skipping later cleanup.

GPose exit and plugin disposal run the same external reset path. A failed
still-live restore remains retryable. Teardown never clears unrelated
Penumbra assignments, Glamourer state, or C+ profiles belonging to another
actor or plugin.

## Pose files: finish the existing path

Do not create another pose service. Keep `PoseFileService`, the existing
`.pose`/`.cmp` parser, exact slot mapping, and current Full/Body/Expression/
Selected options.

Migrate FILES import dispatch through the stable pose edit/history path:

- capture every affected exact slot-qualified target before reset/import;
- Reset-before-import and application form ONE atomic edit;
- a failure restores all captured targets and creates no history item;
- success creates one undo/redo item, including model transform when enabled;
- Selected scope freezes the selection at dialog confirmation and never
  expands into a later selection;
- export remains read-only and writes Character/MainHand/OffHand/Prop/
  Ornament from their exact live slots.

Keep pose actions under Pose → FILES. Do not move them to Appearance and do not
place pose data in MCDF.

## UI

No new top-level window or tab:

- Pose → FILES retains the compact scope/options form and Import/Export row.
- Appearance gains EXTERNAL APPEARANCE and MCDF sections below the existing
  Presentation/Wet surface controls.
- MCDF row: current state/file name, **Import…**, **Export…**, and
  **Reset MCDF** when owned. While busy it becomes one progress row with
  phase, progress bar, byte/file readout, and **Cancel**.
- Plugin rows remain present but unavailable when their plugin/API is absent;
  layout never jumps as plugins load or unload.

Use the existing FileBrowser, Popover, FilterPill, SidebarRow/form metrics,
buttons, sliders, separators, glass chrome, and HoverHelp. Do not introduce
native ImGui combo styling, wrapped instructional paragraphs, debug IDs,
per-pane padding, a permanent scrollbar, or a second status surface. The
Appearance page keeps the current no-inspector width and the main window does
not resize when dialogs/popovers open.

## Implementation order

1. Define MCDF v1 DTO/reader/writer, validation limits, progress/result types,
   and the stable integration-session contract.
2. Implement version-gated Penumbra, Glamourer, and Customize+ runtime ports
   plus exact baseline capture/restore; replace the narrow Glamourer bridge
   rather than layering a second IPC framework beside it.
3. Add searchable Collection/Design/C+ pickers and component resets.
4. Implement MCDF import validation, extraction, transaction, cancellation,
   rollback, redraw reconciliation, and lifecycle teardown.
5. Implement MCDF export capture, compatibility filtering, deduplication,
   atomic output, cancellation, and reporting.
6. Route existing pose import through one atomic history edit and retain the
   current export path.
7. Build the Appearance/Pose UI, integrate Reset All, remove duplicate/dead
   paths, and update ONLY the existing product-boundary, runtime-appearance,
   and files-and-transfer normative documents.

Use reviewable commits without amend/rebase after review starts. Suggested
commits: contract/DTO → external runtime ports → selectors → MCDF import →
MCDF export → pose atomic import → UI/lifecycle/docs. Release is the
non-deployment validation gate. A Debug build is only the announced deployment
action for the exact reviewed head after readiness is confirmed; see
`docs/process/testing.md`.

## Acceptance

- Every action targets the actor captured from actor-or-bone selection; actor
  switching while a dialog is open cannot retarget it.
- Collection, Glamourer design, and C+ profile lists are searchable, apply to
  only the selected actor, survive their required redraw, and independently
  restore the exact incoming assignment/state/profile.
- A valid Brio/Ktisis/Mare MCDF imports onto a selected supported GPose actor:
  files, swaps, manipulations, Glamourer appearance, and C+ scale all appear.
- Reset MCDF restores the actor's prior external state and leaves no Poser
  temp collection, mod tag, profile, lock, extracted file, or stale binding.
- Import failure and cancellation at every phase produce the same cleanup;
  rollback failure is reported and remains retryable.
- Exported MCDF opens successfully in current Brio and Ktisis and reproduces
  the selected actor's supported resources, manipulation data, Glamourer
  state, and C+ scale. Missing/skipped resources are reported, not hidden.
- Export is read-only, cancellation leaves no partial destination, and an
  existing destination survives a failed write.
- Pose import for Full/Body/Expression/Selected is one undoable action;
  Reset-before-import is included in that action; auxiliary slots remain
  exact; `.cmp` cannot zero positions; pose export round-trips with Brio.
- Reset appearance still touches only opacity/tint/wetness. Reset All, GPose
  exit, actor despawn, and plugin disposal clean every Poser-owned external
  override without clearing unrelated external state.
- Actor/bone selection, posing, history, animation, IK, gaze, expressions,
  auxiliary skeletons, and gizmos remain usable after every required redraw.
- The retained UI has no clipping, overlap, permanent scrollbar, duplicate
  tooltip, or width change at supported scales.

Runtime verification must use at least: one vanilla actor, one actor with an
individual Penumbra collection, one saved C+ profile, one pre-existing
temporary C+ profile that must block displacement untouched, one unlocked
Glamourer state, one locked state that must fail untouched, one MCDF with all
payload types, and cancel/failure injection after each mutating import phase.

## Explicitly excluded

- File/pose/character library, thumbnails, tags, favourites, search across
  disk, project folders, cloud download/share, or recent-file database.
- Whole-scene import/export, scene actor creation from a file, cameras,
  lights, environment, references, or scene layouts.
- Character Select+ arbitrary-actor apply until it exposes actor-targeted
  apply AND restore IPC; no config scraping or command/target simulation.
- MCDF scene bundling, pose embedding, animation/keyframe export, Mare
  networking, sync-service impersonation, or exporting another plugin's
  locked state.

## Handoff

Report base/head, commit map, supported API versions and endpoint labels,
MCDF format/limits, exact baseline and ownership model, transaction and
rollback phases, temporary-resource cleanup, export capture/filter rules,
pose-history migration, UI structure, removed paths, Release validation result,
deployment decision, and the remaining in-game matrix. Compilation proves none of the IPC behavior,
redraw reconciliation, format interoperability, cancellation, restoration,
or visual acceptance.
