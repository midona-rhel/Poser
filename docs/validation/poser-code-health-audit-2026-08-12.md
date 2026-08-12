# Poser code-health audit

Date: 2026-08-12. This is a non-normative validation snapshot, not a new
architecture contract. Eight independent Luna Max worktree audits covered
architecture, native lifecycle, application transactions, UI/DRY, files and
recovery, integrations, backlog/docs, and an adversarial cross-cutting pass.

## Verdict

Poser needs a serious cleanup program, but not a ground-up rewrite. The
compile-time `Domain -> Application -> Game -> host` direction, stable
generation-qualified identities, pointer-free application state, explicit
native ports, delta-based pose model, and retained UI are sound foundations.
Replacing them wholesale would discard the parts that currently make actor
churn, redraws, history, and native failures tractable.

The highest-value work is to repair lifecycle and transaction correctness,
add tests around the newer layers, and then extract the overloaded classes one
responsibility at a time. Large changes are warranted inside several classes;
a 70% project rewrite is not supported by the evidence.

Independent verification on this snapshot:

- `dotnet test Poser.slnx -c Release --no-restore --nologo`: 166 passed.
- `dotnet build Poser.slnx -c Release --no-restore --nologo`: succeeded with
  three warnings (one obsolete native draw-object read and two dead library
  caption fields).
- The older untracked backend audit's “zero tests” premise is stale. The test
  project is present and in the solution. The real gap is that current tests
  concentrate on PosingCore; the Application and native lifecycle seams remain
  largely dark.

## Fix first: confirmed correctness and safety defects

### 1. Make GPose exit an explicit ordered transaction

Current correctness depends on synchronous event subscription order, and even
the intended “AutoSave first” order is defeated when the AutoSave factory
eagerly resolves `IPoseFileService`, constructing `PosingService` first
(`Poser/Composition/ServiceRegistration.cs:167-183`,
`PosingCore/Files/PoseFileService.cs:25-31`,
`Poser.Game/LegacyRuntime/PosingService.cs:63-94`). Exit can therefore restore
the actor transform before the final snapshot captures it.

Actor clearing then publishes an actor-list change before the clean lifecycle
restores animation, presentation, Glamourer/Penumbra, and MCDF ownership
(`Poser.Game/LegacyRuntime/ActorManager.cs:79-90,335-355`,
`Poser.Game/Scene/CleanSceneLifecycle.cs:168-207,326-340`). Reconciliation sees
empty bindings and drops captured state as unresolvable.

Create explicit `capture -> restore owned state -> destroy actors/bindings ->
notify` phases. First make AutoSave dependencies lazy; then migrate each owner
into the pre-exit coordinator. Preserve ordinary EventBus notifications, but do
not use subscription order as the teardown transaction.

Tests: fake ordered exit covering actor placement, animation, presentation,
integration and failure; live GPose exit with all five kinds of owned state.

### 2. Defuse failure-prone native initialization

`GazeService` intentionally performs unguarded signature scans and hook enable
(`Poser.Game/LegacyRuntime/GazeService.cs:85-95`). Because it is reached during
eager UI construction, one stale optional signature can abort plugin
construction after earlier hooks/subscriptions were activated, while normal
`Poser.Dispose` is not reached (`Poser/Poser.cs:48-112,168-174`).

Add a host-construction rollback guard that disposes the provider and host
resources after any activation failure. Separately make Gaze use the same
explicit unavailable-capability result used by clean native ports, disposing
partially constructed hooks. Surface central bone-hook and IK capability loss;
do not let edits appear accepted when the apply detour is unavailable
(`Poser.Game/LegacyRuntime/BonePosingService.cs:164-216`).

`IKService` also leaks native allocations when construction fails after only
some blocks were allocated because cleanup is gated on `_initialized`
(`Poser.Game/LegacyRuntime/IKService.cs:52-68,201-209`). Free every non-zero
allocation independently and fault-test each construction step.

### 3. Fix zero-propagation capture before more pose refactors

`TransformComponents.None` is meaningful—apply to this bone without propagating
to descendants—but `PoseLayer.IsValid` rejects it
(`Poser.Domain/Posing/PoseLayers.cs:5-12,55-75`). The runtime then constructs a
domain pose from legacy stacks and can throw during final gesture capture
(`Poser.Game/Transforms/TransformRuntimePort.cs:265-349`,
`Poser.Application/Transforms/TransformGestureService.cs:203-233`). Malformed
layers can instead be filtered away, silently changing history.

Accept every subset of `All`, including `None`; reject unknown bits explicitly
at the runtime boundary and return a typed failure rather than throwing or
dropping the layer. Cover all eight masks through commit, cancel, history,
undo/redo, copy/import, and malformed-mask failure. This is the existing PBI-012
defect and should be the first pose-domain behavior fix.

### 4. Unify transaction outcomes and preserve failed recovery

Gesture, discrete transform, pose-edit, and import paths implement different
rollback policies. Several discard every restore result while claiming the
operation failed atomically (`Poser.Application/Transforms/TransformCommandService.cs:45-51,91-109,141-184`,
`Poser.Application/Posing/PoseEditService.cs:329-370`). Even gesture commit and
cancel lose recovery detail on some paths.

Introduce a small shared outcome—not a generic operation framework—containing
the primary failure, whether rollback ran, rollback failures, and whether
history was appended. When rollback fails, retain a recovery/quarantine record
and prevent another write from starting on split-brain state until retry or
explicit stale-target disposal. Migrate gesture, command, and pose-edit paths;
adapt import/IK's specialized multi-tick state machines to emit the same final
outcome.

### 5. Treat pose import and IK bake as real asynchronous transactions

Pose import currently returns success when it merely schedules the native pass
(`Poser.Game/Posing/CleanPoseFacade.cs:395-446`). UI surfaces consume that as the
final verdict, while completion and rollback occur later and mostly reach logs
(`Poser.Game/Posing/PoseImportCapture.cs:958-1063`,
`Poser/UI/Panes/PoseFileInspectorSection.cs:1699-1777`).

Add a generation-qualified receipt with Pending, Applied, RolledBack, Failed,
and RecoveryRequired outcomes, and notify only the initiating UI/actor
generation. Retain the existing native phase ordering.

Registered import and IK callbacks also lack an active-operation check, so a
timeout/rollback can be followed by a late callback that writes the old stacks
back (`PoseImportCapture.cs:422-515,588-601,964-990`,
`Poser.Game/Posing/IkBakeCapture.cs:313-378,421-452`). Invalidate operation
tokens before rollback and reject every late callback. Add explicit
`CancelPending` recovery for import, IK, and facial capture before actor/plugin
teardown; current disposal mostly unsubscribes and forgets pending state.

### 6. Fix MCDF resource lifetime before splitting its class

MCDF teardown removes the temporary collection, requests a redraw, then deletes
the extracted directory without a redraw barrier
(`Poser.Application/Integration/ActorIntegrationSession.cs:545-575,1202-1238`).
The runtime already provides `RedrawAndWait`
(`Poser.Game/Integration/IntegrationRuntimePort.cs:403-455`). Deletion can race
Penumbra/game consumption of those files.

Make `redraw completed` part of teardown ownership, preserve operation-directory
ownership on timeout, and retry later. Give the session cancellation plus a
bounded shutdown drain so background work cannot call disposed ports or publish
post-dispose progress. Then route path existence, reparse containment, temp
allocation, and file metadata through `IMcdfFileBoundary`; only after these
contracts have tests should the MCDF transaction be extracted from the
1,700-line integration session.

### 7. Replace reusable object-table indices with owned spawn handles

Spawn ownership is stored primarily as `ushort` object-table indexes
(`Poser.Game/LegacyRuntime/ActorSpawnService.cs:31-33,239-272,518-584`). External
deletion followed by slot reuse can make Poser classify or destroy an unrelated
replacement. Post-create failures can leak a native actor, and bulk cleanup
currently forgets indexes even when deletion fails (`ActorSpawnService.cs:112-201,549-584`).

Use a spawn handle containing exact logical generation/address/creation serial
with index only as a lookup hint. Revalidate before every classify, companion,
or destroy operation. On failure retain ownership for retry; rollback every
post-create failure. Make companion polls cancellable and resolve the exact
stable actor each tick rather than capturing a raw address
(`ActorSpawnService.cs:350-401,491-516`).

### 8. Make saved/recovery data durable and validated

Pose, camera, and light saves use direct destination writes, so failure can
truncate the previous valid file (`PosingCore/Files/PoseFile.cs:131-146`,
`PosingCore/Files/CameraFile.cs:94-106`,
`PosingCore/Files/LightFile.cs:141-156`). Implement one same-directory atomic
JSON writer: serialize fully, write/flush a unique temp, then replace/move while
preserving the old destination on failure.

AutoSave cleanup/final capture races its detached worker: CleanOnExit can delete
before a worker creates its directory, or the final exit snapshot can be dropped
by `_writeInFlight` (`PosingCore/Files/AutoSaveService.cs:201-218,238-354,543-571`).
Coordinate worker completion: interval ticks may remain drop-not-queue, but exit
must reserve a final snapshot and cleanup must join/cancel before deletion.

Validate all loaded transforms before planning native writes. Current converters
allow non-finite values and invalid quaternions into the plan
(`PosingCore/Files/PoseFile.cs:119-160`,
`PosingCore/Files/Converters/JsonNumericsConverters.cs:28-38`). Bound clipboard
decompression and library traversal/metadata reads. Detect Anamnesis name
conversion collisions instead of last-write-wins. Fix reset-before-import to
derive reset targets from the same successfully matched scope as writes; today
an unknown-only or filtered file can clear unrelated authored stacks
(`PosingCore/Files/PoseFileService.cs:194-235,268-400`).

### 9. Repair global animation ownership semantics

Global physics ownership is cleared even when native unfreeze fails, eliminating
the application's retry record (`Poser.Application/Animation/AnimationSession.cs:655-663,813-855`).
The runtime can correctly report a failed restore
(`Poser.Game/Animation/AnimationRuntimePort.cs:1091-1143`). Retain owners until
unfreeze succeeds and aggregate reset failure, including physics-only actors.

Speed clear removes runtime enforcement before actor resolution and then writes
hard-coded native speed 1 (`AnimationRuntimePort.cs:730-742,843-853`). A failed
resolution leaves Application ownership and runtime enforcement split; a
successful clear can overwrite incoming game/other-plugin state. Resolve first,
keep enforcement on failure, and define hand-back without an unconditional 1
write. Specify replay-while-paused and display process-global physics as global,
not as selected-actor ownership.

Extract the process-global physics patcher before changing behavior. It currently
NOPs hard-coded instruction spans without expected-byte/instruction-boundary
validation (`AnimationRuntimePort.cs:38,157-165,1091-1174`). Fail closed on byte
mismatch and surface failed unpatch during shutdown.

### 10. Close concrete UI ownership and style leaks

Detached shell reopening bypasses the coordinated open path: UIManager directly
toggles `Main.IsOpen`, so sidebar and toolbar parts remain closed and the user
can get an inspector-only shell with no normal recovery
(`Poser/UI/Composition/UiWindowSet.cs:92-119`,
`Poser/UI/UIManager.cs:250-271`). Route every opener through one primary-window
method and test attached/detached reopen.

Pop-outs create a disposable `GraphicalBonePane` via `ActivatorUtilities` but do
not retain/dispose it, leaking texture wraps and decode continuations
(`Poser/UI/Windows/PopOutWindow.cs:85-96,219-224`,
`Poser/UI/Panes/GraphicalBonePane.cs:44-50,520-537`). They also reuse singleton
animation/appearance/file sections, creating cross-window picker, dialog, status,
and actor-target state. Dispose dynamic panes now; then introduce per-surface
instances for every stateful pop-out pane, sharing only catalogs/runtime
services.

ImGui window and primitive style pushes rely on normal PostDraw/pop paths, and
Modal directly mutates global style. Exceptions can leak style into other
plugins (`Poser/UI/Windows/MainWindow.cs:687-800,970-975`,
`Poser.UI/Primitives/Tags/Modal.cs:78-86,193-198`). Implement PBI-013's small
exception-safe style ledger/RAII scope, migrating one surface at a time while
holding visual hashes stable.

## Structured refactors after the safety net

These are justified extractions, not generic “shorten large files” work:

1. `ActorIntegrationSession`: move all filesystem/resource policy behind its
   boundary; extract `McdfTransaction`; leave vendor selection/orchestration.
2. `AnimationRuntimePort`: extract `PhysicsFreezePatcher`; then separate slot
   speed/lips and stance/emote adapters only where tests prove independent
   contracts.
3. `BonePosingService`: extract pure pose-stack/carryover policy; leave hooks
   and per-frame native application in the native service until live parity is
   proven.
4. `LiveTestService`: move scenario bodies and report validation into focused
   classes, but keep the runner in the Game assembly as a production-wired gate.
5. `MainWindow`: extract pure sidebar projection and typed row capabilities;
   keep `AppShellView` and `ShellSidebar` as the retained renderer/cache rather
   than inventing a component framework.
6. Composition: split service registration into per-feature methods and add an
   explicit ordered lifecycle participant catalog only after exit phases are
   modeled. Do not hide ordering in a generic unordered `IEnumerable`.

Native reads in `GraphicalBonePane` violate the rendering-only boundary
(`Poser/UI/Panes/GraphicalBonePane.cs:275-305`). Move that single race/head-map
query behind a Game read port first. Later quarantine the remaining unsafe
`PosingCore` entity/expression reads and rename LegacyRuntime namespaces one
service at a time; do not combine this with the correctness fixes above.

## Test program

The existing 166 passing tests are useful, but do not protect the newer
transaction/lifecycle contracts. Before major extractions, add Domain and
Application contract tests (separate projects or equivalent) with fake ports:

1. propagation masks, exact bone identity, history, rollback aggregation;
2. scene/generation reconciliation and stale wrapper refusal;
3. animation ownership, failed release, replay/reset semantics;
4. pose import/IK operation tokens, timeout, late callbacks, disposal;
5. GPose exit phase order and startup failure cleanup;
6. MCDF redraw barrier, cancellation, shutdown, orphan retry;
7. spawn index reuse and failed deletion ownership;
8. golden/invalid file formats, atomic writes, AutoSave races, library bounds;
9. multi-window disposal/state isolation and exception-safe style fingerprints;
10. live cancellation cleanup and deep state restoration.

The live harness itself needs repairs before it is authoritative: cancellation
currently skips cleanup, startup persistence failures can leave `IsRunning`
wedged, report writes/validation are weak, and cleanup checks mainly actor count
and selection rather than actor identity, transforms, pose stacks, animation,
and owned state (`Poser.Game/Validation/LiveTestService.cs:110-153,340-377,1215-1248,1352-1543`).

## Documentation and backlog cleanup

Do this early because contradictory instructions cause AI-authored divergence:

- `docs/architecture/product-and-boundaries.md` calls registered, reachable
  cameras, lights, libraries, autosave, animation, and related features deferred.
- `docs/process/testing.md` records eight live scenarios and manual in-game
  visual acceptance; the retired synthetic UI laboratory is not a test gate.
- The older backend audit is useful evidence but false about current tests and
  some reachable directories/events. Keep it only as a dated non-normative
  validation snapshot, update it, or delete it after verified findings become
  PBIs.
- PBIs use `Ready` for both unimplemented work and code awaiting in-game
  acceptance; PBI-015 has duplicate IDs; several completed/superseded UI plans
  retain long implementation prose. Adopt `Proposed`, `Ready`, `Implementation
  present`, `Acceptance pending`, `Accepted`, `Superseded`, `Parked`, and `Audit`.
- Keep PBI-012 and PBI-013 active. Add focused PBIs for async import/recovery,
  GPose exit ordering, MCDF redraw lifetime, spawned ownership, file durability,
  and multi-window composition. Do not reopen features merely because an older
  checklist called them missing.

## Recommended implementation sequence

1. Add Application/Domain characterization tests and correct normative docs/PBI
   status. No runtime change.
2. Fix startup failure atomicity/Gaze/IK allocation, then PBI-012.
3. Introduce the shared transaction outcome and repair rollback reporting.
4. Add async import receipts and operation tokens; cancel pending capture work.
5. Introduce explicit GPose-exit phases and migrate AutoSave/restoration owners.
6. Fix MCDF redraw barrier/shutdown and spawn ownership.
7. Add atomic files, strict validation, exact reset planning, and AutoSave worker
   coordination.
8. Repair animation ownership and isolate/validate the physics patcher.
9. Fix shell/pop-out/style ownership; add per-surface composition.
10. Perform the structured extractions and native-boundary moves one seam at a
   time, keeping existing facades/adapters as rollback points.

Every behavior-changing slice should land with a fake-port contract test and a
targeted live scenario where native timing matters. Avoid parallel edits to the
same large class until a boundary has been extracted.

## Preserve these invariants

- exact stable IDs/generations and slot-qualified bones;
- pointer-free Domain/Application state and framework-thread refusal;
- delta-pure pose export, frozen gesture baselines, one history patch per edit;
- named animation/expression layers excluded from manual history;
- per-aspect ownership and restore-only-what-Poser-changed semantics;
- current native phase ordering where Brio/Ktisis compatibility requires it;
- retained UI, generated token/category artifacts, and UI conformance tooling;
- transitional `CleanPoseFacade`/`CleanTransformFacade` until their callers move.

Do not replace these with raw addresses, a generic mediator/capability bag, a
blanket service interface layer, or a new widget framework.
