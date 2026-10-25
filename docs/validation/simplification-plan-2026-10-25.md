# Whole-application simplification plan — 2026-10-25

Tracking: [#610](https://github.com/midona-rhel/Poser/issues/610). This is the implementation sequence, not a claim that the refactor is finished. Existing contracts remain in [docs/README.md](../README.md); update those homes when a change is implemented.

## Goal and limits

Make each behavior easier to understand, give its state and resources a clear owner, and remove competing implementations. Deliver small changes that can be reviewed, tested, deployed and reverted separately. Do not replace the application architecture in one release.

An actor should have an understandable account of the state Poser applies to it. That does not require moving every service into a giant Actor class. Keep specialist services for animation, posing and integrations. Bring ownership together one behavior at a time, without duplicating their state in a second master object.

The source audit covered 1,075 C# files and 24 scripts/build/configuration files at `04b09fd7272de82035c207e51e13a428595612bd`. [Coverage](audit-2026-10-25/coverage.csv) records inspection, not proof that every comment is clear or every guard is justified. Follow-up reads found remaining jargon. Native metadata and SVG tables were classified as data; their comments and executable helpers were inspected, not every offset or rendered icon independently verified.

## Rules for every change

- One behavior per change. Name the existing owner, the callers to migrate, and the old code to delete before starting.
- Reuse an existing service or store first. Introduce a new type only when it replaces a concrete responsibility; do not add a generic manager, command bus, cleanup framework or second history system.
- Keep one authoritative copy of each piece of state. A temporary forwarding method may help migrate callers, but it must not maintain a second implementation and must be removed in the same completed step.
- Keep file formats, undo behavior and UI behavior unless the issue explicitly changes them. A proposed product change is not a refactoring prerequisite.
- Rewrite comments in touched code as ordinary sentences explaining what happens and why. Remove narration, unexplained shorthand, historical arguments and unsupported guarantees.
- A guard needs a reachable production caller, an actual scheduling boundary, and a meaningful consequence. A test inventing two instantaneous clicks is not enough. Loads waiting for files, redraws or another frame can overlap at ordinary speed; trace that path before deciding.
- Put a justified check at the owner of the operation. Remove repeated checks only after confirming they protect the same interval. Keep native lifetime checks and numerical requirements where memory access, division or inversion actually occurs.
- Record progress in the existing issue and this plan. An added helper is not completion while the duplicate behavior still exists.

## First delivery sequence

These are the first changes, not the complete scope. The whole-application work packages and completion requirements follow this sequence.

### 1. Correct the small, proven allocation error — #637

**Scope:** `NativeHelpers.AllocateAlignedMemory` and its three IK callers. Correct padding so an already aligned pointer needs no extra movement. Keep allocation and freeing paired. Do not redesign IK or claim this explains a past crash.

**Remove:** the incorrect padding expression. **Verify:** all 16 address remainders preserve alignment and the requested memory extent; inspect the paired frees. No deliberate live memory corruption.

**Done when:** [#637](https://github.com/midona-rhel/Poser/issues/637) is independently reviewed and its focused checks and Release validation pass. This can ship without any ownership refactor.

### 2. Establish actor ownership through clothing-only — #610, first slice

Start by writing a small ownership table in the issue from actual callers: what is stored, who changes it, who releases it, and what happens on redraw, actor removal and GPose exit. Include `ClothingOnlyControl`, `InvisibleSkinService`, `CharacterModelNative`, the binding registry and existing actor removal paths.

Then implement only this behavior:

1. Keep `ClothingOnlyControl` as the shared entry for UI, scene restoration and bridge calls.
2. Make the actor's clothing-only state and active work explicitly belong to its current lifetime. Reuse existing actor identity and lifecycle notifications; do not invent another generation system.
3. Route redraw invalidation and actor/session removal through one cleanup responsibility. Stop or invalidate pending work before it can write to a released body. Retain an actual failed external cleanup only when there is something left to release.
4. Keep native addresses and model resources in Game. Keep application state as IDs and values. Shared Penumbra collections remain collection-owned; actor cleanup must not release another actor's shared work.
5. Preserve the corrected `CharacterModelNative.ReleaseSlot` path: release the model through its native reference-count operation. Do not manually reset its count, release only its resource handle, or release it a second time during actor cleanup.

**Remove:** separate bookkeeping or cleanup branches made redundant by that owner, including lazy stale-state pruning if explicit lifecycle handling replaces its purpose. Do not delete it merely because it looks defensive.

**Verify:** apply clothing-only, redraw, remove the actor, exit/re-enter GPose, and save/load a scene containing the setting. Check the normal result and that no pending operation writes to the retired body. Check a shared collection only if the traced path actually shares temporary resources.

**Done when:** the issue's ownership table matches the code; all entry points reach the same behavior; state is invalidated/released through the documented lifetime; focused tests and local user acceptance pass. No animation or transform migration is required for this step.

### 3. Make actor transform results truthful — #631

Keep `PosingService` as the native write owner and `TransformRuntimePort` as the application adapter. Return whether the write happened; update callers such as parenting to use that result. Remove the positive-only scale clamp that contradicts accepted authored values. Keep legitimate finite-value checks; establish any actual native scale limit instead of choosing an arbitrary one.

**Remove:** unconditional success after void setters and the contradictory clamp. **Verify:** an existing small Customize+ scale, negative mirroring, requested versus read-back values, undo/redo, and an unavailable actor. Do not expand this into every imaginable numerical input.

**Done when:** [#631](https://github.com/midona-rhel/Poser/issues/631) preserves the intended data and refuses truthfully. Do not move the whole transform service into an actor object as part of this fix.

### 4. Correct transform conversion one caller family at a time — #632

Map the actual uses of `Transform.FromMatrix` first. Preserve authored components where they exist. Handle reflection, collapsed axes and shear according to what each operation needs; an SRT value cannot represent arbitrary shear. Require an inverse only where one is computed.

Start with one affected caller family, then migrate the remaining callers in separately reviewable changes. A temporary forwarding API must use the corrected implementation; it must not create a competing conversion policy.

**Remove:** invented 0.01 scale and duplicate conversion work as callers migrate. **Verify:** equivalent geometry for the chosen caller, reflection/collapsed axes where supported, and the relevant normal posing/collider path. Delete the old conversion only when callers are migrated. Track in [#632](https://github.com/midona-rhel/Poser/issues/632).

### 5. Delete inactive autosave writing — #635

Keep `SceneAutoSaveService` as the active writer. Trace legacy root lookup, recovery records and backup browsing before removing dependencies. Move required read-only behavior to an existing library/configuration owner, then remove unused pose capture, writer, status and registration paths.

**Preserve:** all old files, legacy browsing and meaningful recovery data. **Remove:** inactive writing and obsolete tests that only describe that deleted writer. **Verify:** scene snapshot, retention/status, expected exit behavior and opening an old pose backup. Track in [#635](https://github.com/midona-rhel/Poser/issues/635).

### 6. Reuse import and placement rules — two independent changes

**6a — #634:** Define import mode/type/scope eligibility once in the existing pose-import UI owner. Keep separate menu and dialog layouts; delete their duplicated predicates and labels. Check the same ordinary pose, CMP and selective-import choices in both surfaces. Do not build a new form framework. [Issue](https://github.com/midona-rhel/Poser/issues/634).

**6b — #636:** Route pane camera/light entry creation through `LibrarySceneActions.SpawnEntry`. Delete `ScenePane.LoadEntry` option construction. Preserve additive placement, selection and notices. Check entry creation from the pane and Library, including the existing missing-anchor fallback. [Issue](https://github.com/midona-rhel/Poser/issues/636).

These changes do not depend on a complete actor-state migration.

### 7. Consolidate one resource/recovery path per change — #610 follow-ups

Use the caller evidence in the [Application plan](audit-2026-10-25/application-plan.md). Create or link a bounded implementation issue before each slice; do not combine these into one cleanup PR.

- **MCDF cleanup:** keep `McdfTeardown`; pass the intended restoration target explicitly, then remove duplicated resource release from `McdfImport.Rollback`. Preserve immediate pre-import rollback versus original-baseline Reset. Check import/reset and one real cleanup failure followed by retry.
- **Pose-edit recovery:** make returned and thrown write failures use the existing restoration path in `PoseEditService`. Check a second write failing after the first succeeds. Do not introduce another transaction system.
- **Scene-save temporary files:** make the existing save attempt own its temporary files across its early returns. Check validation refusal and successful output; remove success-only cleanup.

A discovered native correctness defect can be fixed independently ahead of these steps when evidence warrants it. Record the reprioritization; do not silently expand a refactor.

### 8. Continue through the actor and application work packages below

Clothing-only establishes the first concrete ownership example. Continue through appearance, animation, transforms, pose/IK and gaze, then the scene-wide behaviors. Each area must finish its ownership review and implement supported simplifications. The program does not end after evaluating the first example.

Reuse what worked in clothing-only, but do not mechanically copy a pattern that added layers without removing responsibility. The detailed packages below define the rest of the work, dependencies and acceptance.

## Verification and completion for each step

1. Describe the real starting state, action and expected result in the issue. Distinguish source evidence from a reproduced symptom.
2. Implement and review the complete small change, including deletion of superseded code and plain-language comments.
3. Run focused behavior checks, then the required Release build/test gates. Reuse recorded evidence; do not rerun unrelated live matrices after every edit.
4. Deploy native/UI changes through [the existing testing contract](../process/testing.md). Give the user a short actionable test card; compilation is not user acceptance. Documentation-only changes need no game deployment.
5. Merge the accepted step independently. Record the commit, checks and remaining uncertainty before starting the next dependent step. Each step must remain independently revertible; do not require an unfinished later migration for correctness.

Report production code, tests and documentation separately. Reduced line count is useful evidence, not a quota. Completion means one owner, obsolete paths removed, behavior checked and explanations understandable—not merely fewer files or more interfaces.

## Intended structure across the application

This is the target division of responsibility, not a demand to rename every class or create every illustrated property immediately.

| Responsibility | Authoritative owner | What must not own a second copy |
|---|---|---|
| Actor identity and native lifetime | Existing binding/spawn ownership code in Game | UI panes, feature-specific address registries |
| Values Poser has applied to an actor | Actor-associated state, organized by appearance, animation, transform, pose and gaze | Parallel service dictionaries containing the same values |
| Native models, hooks, allocations and current skeleton resolution | Game-side owner with explicit release/invalidation | Application actor data or UI state |
| Shared provider resources | Existing integration owner, with explicit actor users | Each actor independently pretending it owns a shared collection |
| User commands and undo | Existing application feature controls and history | UI callbacks, scene importers or bridge handlers implementing their own edits |
| Scene membership and saved snapshots | SceneSession and existing scene workflow/capture owners | An actor object that independently decides scene membership or persists itself |
| Selection and groups | Existing selection/group controls | Window-local copies treated as scene state |
| File formats and storage | Documents, called by application workflows | Panes interpreting or writing the same format independently |
| Presentation and temporary editing drafts | UI | Native/application owners retaining window-specific layout or hover state |

The actor view should make it possible to answer “what has Poser applied to this actor?” without searching unrelated dictionaries. It may initially expose existing state by reference/read methods. That exposure is not the migration: for each migrated area, choose one authoritative store, move its callers, then remove the old storage. Do not create a serialized master Actor containing live pointers, every UI setting, and shared resources.

Separate three lifetimes explicitly: the logical actor, its current native body/skeleton, and resources shared beyond it. A redraw replaces a body without necessarily discarding the authored pose. Removing an actor ends its pending operations. Removing one actor does not destroy a shared collection still in use. Use the existing identity/version machinery to express these distinctions.

## Whole-application work packages

Each numbered substep below is a candidate small PR, not one giant PR per package. Where evidence is conditional, the first substep establishes the real caller and result. A package can retain a sound existing design with recorded evidence; it cannot be closed by proposing another audit later.

### W01 — Documentation and names throughout the repository (#633)

**Coverage:** every source comment, XML summary, relevant identifier and normative architecture/feature document, including tests, scripts, generated comments and handwritten code in generated-named files.

1. Separate “file inspected” from “editorial standard met” in the existing ledger. Review every comment for comprehensible sentences, defined terms and agreement with callers. Correct the remaining prose; do not treat the prior coverage count as completion.
2. Work by subsystem with its behavior package. Rewrite comments that explain necessary reasoning; delete comments that merely repeat the code, obsolete incident stories and claims the implementation cannot establish.
3. Rename internal abbreviations and misleading methods where the new name materially clarifies behavior. Keep mechanical renames separate from behavior fixes. Do not rename serialization fields, IPC contracts or native layout members without compatibility handling.
4. Rewrite the existing architecture/feature homes as they are reconciled with implementation. Explain normal operation first, then actual constraints. Remove duplicate explanations and superseded documents; preserve license/provenance facts.
5. For generated output, update the generator template when available. Record absent external templates explicitly instead of pretending the next regeneration preserves the change.

**Delete:** jargon-heavy narration, contradictory summaries, duplicate contract prose and unused aliases established by caller search. **Verify:** every comment reviewed, source-token equivalence for comment-only patches, compilation for renames, and plain-language explanations that do not require the reader to already know the architecture. This work runs throughout all waves, not only the opening patch.

### W02 — Remove unjustified guards and failure machinery across all packages (#610)

**Coverage:** early returns, busy checks, cancellation/retry logic, range clamps, identity checks, timeouts, fallbacks and tests asserting those restrictions.

1. In each package, record the actual protected action, callers, scheduling interval, consequence and existing protections. Classify the guard as necessary, duplicated, wrong policy, unreachable under supported calls, or still unproven.
2. Remove guards and special branches whose only justification is an impossible interaction or an internal fake caller. Remove tests that only preserve that obsolete restriction. Do not remove a protection merely because its failure has not happened during one session.
3. Consolidate duplicate checks at the existing behavior owner. Distinguish checks before an asynchronous wait from checks after it: those may protect different objects or states.
4. Replace silent correction or fabricated success with truthful outcomes where the normal operation can actually fail. Preserve authored small/signed scale; require invertibility at the operation computing an inverse, not everywhere data is stored.
5. Simplify excessive cancellation/retry state only after identifying what unfinished work owns. Do not create exhaustive interruption matrices or new locks without a reachable production interleaving.

**Deliverable:** a short decision record per behavior, with removed code and unresolved evidence, maintained in its issue. **Done:** every package has reviewed its guards; remaining checks have concrete reasons; unproven stories are not quietly implemented as new requirements.

### W03 — Actor state, creation, redraw and removal (#610)

**Start:** clothing-only first delivery step. **Current pieces:** IActor, binding registry, actor/spawn managers, lifecycle owners, feature stores and pending operations.

1. Inventory actor-associated values and resources across the app, including appearance, animation, transforms, pose/IK, gaze, companions, presentation and integration work. For each, name its current storage and lifetime. Include histories that deliberately retain restoration data.
2. Establish the actor-associated state access described above using existing identity. Migrate clothing-only first. Add the next state area only as W04–W07/W11 migrate its actual callers and remove old ownership.
3. Give ordinary removal, scene clear, GPose exit and shutdown one applicable actor retirement sequence through existing lifecycle code. Order feature cancellation/invalidation and resource release before native deletion where required; preserve borrowed-actor restoration and deletion undo.
4. Validate the captured source identity before deferred duplicate collection access, not only the later appearance copy. Handle model redraw refusal before disabling drawing when the completion poll cannot be scheduled.
5. Remove dead lifecycle helpers and feature-specific teardown subscriptions once the shared actor lifetime actually replaces them. Keep explicit exceptions for shared resources and skeleton replacement.

**Verify:** ordinary spawn/duplicate, redraw, remove/undo, scene clear and GPose exit using recorded fixtures. Verify one evidenced deferred source-removal case. **Done:** each actor-associated area has a named owner and removal behavior; no feature must discover retirement solely by a later read pruning its dictionary. This does not require moving all implementation into Actor.

### W04 — Appearance, clothing-only and external integrations

**Current owners:** ClothingOnlyControl, InvisibleSkinService, ActorAppearanceControl, integration sessions, McdfImport/McdfTeardown and provider adapters.

1. Complete clothing-only ownership and truthful temporary-mod cleanup results. Preserve the fixed native model release operation.
2. Consolidate MCDF rollback/reset resource release in McdfTeardown while passing the different restoration baselines explicitly. Migrate UI, scene and history callers through the existing application controls.
3. Keep failed temporary profile/collection deletions identifiable until released or confirmed absent. Remove bookkeeping only after the provider outcome permits it. Distinguish provider errors from “no active profile.”
4. Move actor-specific applied appearance/profile state into its chosen authoritative actor-associated store; keep shared collection resources at the integration owner. Separate visible progress from worker completion when files/resources still belong to unfinished work.
5. Consolidate appearance read/apply/restore policy and remove fire-and-forget alternatives that lose failures. Preserve the external tools' ownership of their own saved designs and profiles.

**Verify:** normal apply/reset, replacement MCDF, scene restoration, actor removal and one source-backed provider refusal/retry. **Done:** the actor state explains what is applied; integration ownership explains what is shared or retained; no second cleanup algorithm remains.

### W05 — Animation, expression and physics state

**Current owners:** AnimationSession, its mode-specific helpers, AnimationOverrideStore, facial/idle capture and Game runtime ports. Reuse the existing animation owner; do not create another animation state machine.

1. Map pause, play, speed, scrub, stance and expression state to their current callers and native writes. Move authoritative actor values into the actor-associated organization without maintaining a mirrored override dictionary.
2. Propagate actual per-layer write results through Resume/ResumeUntracked. Make history reflect successful partial edits where that is the supported policy; remove unconditional success and duplicated reset/restore handling.
3. Make facial capture setup, completion and cleanup use one operation owner. Preserve pending recovery when a native release actually failed; remove pending work left behind by a failed start.
4. Reconcile physics-patch requested state with bytes actually changed after a write failure. Keep allocation/hook cleanup with the native owner rather than spreading native recovery into actor/UI classes.
5. Remove obsolete animation paths and explanations only after the current UI, scene and history callers have moved. Preserve Play/scrub semantics unless a separately evidenced product bug requires change.

**Verify:** existing normal pause/play/speed/hold fixtures and the specific refused-write/setup cases. Do not reopen the previously excluded broad animation test exploration. **Done:** one authoritative actor animation state and one implementation of each action/restore behavior; comments describe actual timing rather than promises.

### W06 — Transforms, parenting, selection and groups (#631, #632)

1. Complete truthful actor writes and scale preservation. Trace every consumer before changing shared transform conversion.
2. Consolidate repeated exact-target lookup in TransformTargetResolver while preserving which entity kinds each action supports. Share equivalent transform math rather than collapsing absolute, additive and multiplicative values into one ambiguous type.
3. Separate approximate pose comparison from equality used for hash keys. Inspect actual callers, migrate them explicitly, and remove the conflicting Equals/hash behavior.
4. Keep gesture capture, commit, cancel and restoration at the existing transform owner. Remove separate entry-point policy from inspector, overlay, group and bridge paths where it duplicates that owner.
5. Account for actor transform and parenting state through the chosen actor/scene owners. Keep selection ordering and group membership in their existing scene-level owners; do not duplicate them inside every actor.

**Verify:** drag/cancel, typed edit, parent/group move, selected/global undo and authored scale with normal fixtures. A rollback restriction is narrowed only after establishing what incomplete restore it protects. **Done:** one write/result policy, shared equivalent math, and consistent entry-point behavior.

### W07 — Bone pose, IK, colliders and skeleton replacement

**Current pieces:** PoseStackStore, BonePosingService, BoneApplyPass/BoneSnapshotPass, skeleton bindings, IkChainRegistry, capture services and collider builders.

1. Separate persistent authored pose/IK settings from native indices and caches resolved for one skeleton build. Preserve actor/slot/name-based pose retention and invalidate/rebuild derived native chains on skeleton changes.
2. Bring IK setup/bake pending work under one operation owner before its first mutation. Reuse the same stop/restore path for setup failure and cancellation; history resolves saved identities rather than retaining obsolete bone wrappers.
3. Make bone-batch completion independent of whether the UI requests display snapshots. Remove repeated work left alive because optional display reads did not occur.
4. Fix native allocation padding/partial-init cleanup and explicit Vector3-to-native-vector writes in small changes. Share existing native helpers only where their ownership and layout actually match.
5. Consolidate bone-to-world frame use across overlay, gizmo, IK and collider capture. Keep renderer-completed Customize+ scale. Remove a competing frame/conversion only after its callers use the established one.
6. Review collider fit data, chain limits and numerical guards against actual geometry/solver requirements. Preserve supported missing-bone behavior; do not invent geometry or reject an entire actor to satisfy a convenient helper.

**Verify:** saved pose import, redraw retention, the existing rope/collision/scale fixture, chain rebuild and a targeted failed bake setup. **Done:** persistent user settings survive intended redraws; derived pointers/indices cannot silently carry over to a different skeleton; UI display demand does not own pose execution.

### W08 — Undo, redo, results and recovery

1. Trace history recording and replay for each migrated behavior. Use the existing journal and lifecycle history; remove UI-created or service-local duplicate inverses.
2. Define result handling at each behavior owner: success, an expected no-op, an unavailable target, or an actual failure. Reuse existing result types where suitable; do not invent a universal result framework just to standardize names.
3. Make returned and thrown failures enter the same recovery path where earlier writes have occurred. Preserve recovery data only for real unfinished restoration, not generic hypothetical future failures.
4. Review history-clear/eviction subscribers so each retained external resource has one release owner. Include actor deletion undo, selected-entity history and partial operation outcomes.
5. Fix target/reset gaze history after deciding its existing user-facing scope; do not call a history expansion a purely mechanical refactor.

**Verify:** ordinary change/undo/redo, actor removal/restoration, history eviction with retained resources and one demonstrated partial failure. **Done:** replay uses the same behavior as direct commands, and failed/partial writes do not become fabricated successful history.

### W09 — Scene capture, load, save and autosaves (#635)

1. Use feature owners to capture and restore actor/scene state. Remove scene-specific reimplementations of appearance, animation, transforms and gaze as their packages migrate.
2. Give save temporary files one owner covering all exits. Keep asynchronous readiness and operation completion distinguishable; remove comments treating a request being accepted as the state already restored.
3. State the existing partial-load policy clearly. Different treatment of invalid records, spawn failure and readiness timeout is not automatically a bug; change it only for an agreed user behavior.
4. Remove inactive pose autosave writers and status wiring. Retain legacy backup browsing/root lookup and required recovery records. Keep camera capture/restoration and same-map placement behavior intact.
5. Consolidate capture/write/retention policy only where the manual-save and autosave behavior is truly shared. Record an explicit decision on skipped-busy ticks instead of adding a retry queue by default.

**Verify:** saved scene reload, missing local collection fallback, whole-scene autosave including camera, legacy browsing and one evidenced failed save. **Done:** one implementation per feature restore, no inactive writer graph, no second scene-state model maintained by UI.

### W10 — Library, import/export, preview and document storage (#634, #636)

1. Share import option rules between layouts and route pane entry placement through the existing library action owner.
2. Trace path decoding/planning between PoseFileInspectorSection and NativePoseImportService. Remove repeated reads/orchestration only where the same request actually performs them twice. Preserve Smart/CMP behavior.
3. Make preview observe the final import result and its own request identity before presenting success. Keep preview actor/native teardown with its existing lifetime owner.
4. Consolidate file failure reporting at the existing codec/storage boundary. Preserve legacy format readers, source-folder semantics and shared scene compatibility.
5. Review scanner bounds, hash lookup and metadata caching with real inputs. Do not add stricter size limits, reject linked folders or hash every scan solely for hypothetical cases.

**Verify:** file/clipboard/library import, export, preview replacement at ordinary speed, old formats and normal source-folder browsing. **Done:** layouts share policy; actual loading/capture has one owner; the library does not pretend preview admission proves completion.

### W11 — Cameras, gaze and viewport state

1. Propagate camera switch/target restore outcomes before updating dependent locks, labels and history. Remove successful results after refused native work.
2. Keep follow/pivot state owned by the relevant camera. Establish the intended relative-coordinate tracking behavior before replacing world-origin-dependent math; clear or transfer accumulated input deliberately on camera switches.
3. Move authoritative actor gaze state into its actor-associated owner while retaining the native driver. Share forward-point calculation, and only mark native target state applied after the write succeeds.
4. Route panel and gizmo target edits through the same application actions/history. Preserve per-window view state separately from camera/actor state.

**Verify:** switch between cameras, tracked target move, target removal/undo, gaze point/part lock and pose-aware gaze. **Done:** no shared stale pivot between unrelated cameras, truthful outcomes and one actor gaze state; no additional camera framework.

### W12 — Lights, world objects, environment, VFX and companions

1. Preserve the different ownership rules: destroy Poser-created objects, restore borrowed objects, and retain shared resource ownership. Reuse common mechanics without replacing all removal with one universal Delete.
2. Apply the existing world-object identity check to deferred native writes that currently check only address liveness. Carry allocation identity through initial spawn failures as respawn already does; remove address-only cleanup alternatives.
3. Make lighting hook availability mean successful activation, and release failed partial hooks. Preserve the previous gobo on refused replacement or report the actual partial outcome.
4. Make festival changes compose against pending desired state rather than overwriting each other with stale snapshots. Keep original-state restoration owned until it is actually applied.
5. Consolidate AVFX chunk traversal around the actual supplied buffer bounds; review graph traversal limits inside collection as well as processing. These protect native reads, not imagined user clicking.
6. Reconcile companion state with its own native actor lifetime and its owner's attachment operations. Do not migrate a replaced companion's state to a different body accidentally.
7. Review overlays/reference images and their native/resource lifetimes through existing presentation owners. Share selection/history behavior without making their window state part of the scene entity.

**Verify:** normal create/change/remove/undo for each entity kind, borrowed-object restore, companion replacement, ordinary environment changes and the specific source-backed failure cases. **Done:** one state/write/release owner per entity behavior, with intentional ownership differences preserved.

### W13 — UI commands, windows, widgets and input

1. Trace sidebar, inspector, context-menu, toolbar, popup and overlay actions to application owners. Remove duplicated eligibility and mutation policy; keep presentation-specific layout and transient state local.
2. Consolidate widget contracts already intended to be shared: width/row behavior, menus, selectors and feedback. Remove ignored parameters and misleading APIs after checking callers; do not build a replacement UI framework.
3. Fix the reserved-but-unwritten skeleton connector geometry in a focused patch. Treat other visual observations as needing the corresponding real UI check, not proof from a screenshot-free test.
4. Standardize view-model initialization for open/reset/reload where wiring currently differs. Review input down/up handling and binding suppression through actual hook/fallback routes.
5. Review window lifetime subscriptions, pop-out state and preview/editor resources. Remove stale callbacks through the existing window owner rather than feature-specific defensive checks in every draw.

**Verify:** relevant actions from both entry points, a normal window open/close cycle, binding edit/reset, and user acceptance of visible changes. **Done:** UI describes and invokes behavior; it does not privately implement it. No new resize/scale test campaign unless the change actually affects that behavior.

### W14 — Settings, presets, catalogs and persistent configuration

1. Keep ConfigurationService responsible for applying/persisting settings. Trace drafts, reset, migration and change notifications; delete separate apply rules in UI where they duplicate it.
2. Retain genuine multi-window edit protection where two editors can remain open at ordinary speed. Remove a stale-draft guard only after proving that route cannot occur or replacing it with the intended merge policy.
3. Consolidate preset create/copy/save/delete/default selection within existing preset owners. Preserve locked defaults, separate Body/Face presets and portable bone keys. Keep editor layout changes out of actor pose history.
4. Establish catalog instance and load lifetimes. Expose a real failed load where needed; do not add synchronization merely because a Boolean is not a lock. Remove static cross-instance state only where ownership warrants it.
5. Keep file compatibility aliases required by existing configurations; delete dead code aliases only after caller/format checks. Make every retained limit explain an actual product or storage requirement.

**Verify:** settings save/reset/reload, old configuration load, preset edit/copy/delete, and actual catalog retry behavior. **Done:** one settings/preset policy owner, no hidden UI-specific persistence path or unsupported concurrency machinery.

### W15 — Native boundaries, diagnostics, build tools and final removal

1. Complete the native mutation inventory across hooks, model slots, vectors, skeletons, objects and resources. Route equivalent operations through established helpers. Do not consolidate operations with different native ordering just because their signatures resemble each other.
2. Keep thread/identity validation at the native access boundary, with re-resolution after meaningful waits. Remove redundant callers' checks once the boundary enforces the same contract; preserve checks for native work that bypasses that resolution.
3. Ensure bridge/testing commands call production actions rather than implementing a second behavior path. Diagnostics observe and report; they must not become hidden state owners or silently repair game state.
4. Remove dead services, composition registrations, obsolete test fixtures, unused compatibility wrappers and superseded documentation after their callers have migrated. Check build scripts and generated-source provenance without changing external formats or release behavior casually.
5. Reconcile the ownership map against the finished source and each work package's deletion evidence. Update normative documentation once per contract, linking rather than repeating it.

**Verify:** dependency boundary check, Release build/tests, diagnostic export, and targeted native acceptance already recorded by the relevant packages. **Done:** no abandoned parallel implementation or migration scaffolding remains; diagnostics and tests use the same behavior owners as the product.

## Delivery waves and dependencies

| Wave | Required delivery | Dependency / exit |
|---|---|---|
| A: establish the baseline | Start W01/W02 decision ledgers; fix #637; complete the W03 clothing-only ownership slice | Existing interfaces/state documented accurately; first native cleanup behavior accepted |
| B: actor behavior | W04 appearance, W05 animation, W06 transforms, W07 posing/IK, actor gaze from W11 | Migrate one state area at a time; W08 checks history for each before that slice closes |
| C: workflows and persistence | W09 scenes/autosaves and W10 library/import/preview | Use migrated feature owners; #635/#634/#636 may proceed earlier because they are independent |
| D: scene-wide behavior | Remaining W11 cameras and W12 world/lights/environment/companions | Preserve owned/borrowed/shared distinctions; coordinate only where actor state is actually consumed |
| E: interaction and configuration | Finish W13 UI and W14 settings/presets/catalogs | UI routing improvements can accompany earlier packages; their remaining review cannot be omitted |
| F: close the migration | W15 removal/reconciliation; finish W01 readability and W02 guard decisions everywhere | All packages have evidence, deletions or a reasoned retain decision; no “audit later” area remains |

W01, W02 and W08 are continuous obligations across waves. Independent correctness fixes need not wait for a large ownership migration. Work on one shared subsystem at a time; parallel work is appropriate only when explicitly delegated and ownership does not overlap.

## Tracking and whole-program completion

Issue mapping created 2026-10-25. These package trackers do not replace the smaller implementation issues. #637 is implemented locally in `349627d1`: Release build and 913 tests passed; review/finalization remains. W03/W04 tracing starts in [#652](https://github.com/midona-rhel/Poser/issues/652), with its clothing-only ownership table. No deployment or whole-package completion is implied.

| Package | Issue | Initial status |
|---|---|---|
| W01 | [#633](https://github.com/midona-rhel/Poser/issues/633) | Continuing readability review |
| W02 | [#638](https://github.com/midona-rhel/Poser/issues/638) | Not started |
| W03 | [#639](https://github.com/midona-rhel/Poser/issues/639) | Implemented #652; awaiting live acceptance |
| W04 | [#640](https://github.com/midona-rhel/Poser/issues/640) | Implemented #652; awaiting live acceptance |
| W05 | [#641](https://github.com/midona-rhel/Poser/issues/641) | Not started |
| W06 | [#642](https://github.com/midona-rhel/Poser/issues/642) | Not started |
| W07 | [#643](https://github.com/midona-rhel/Poser/issues/643) | Not started |
| W08 | [#644](https://github.com/midona-rhel/Poser/issues/644) | Not started |
| W09 | [#645](https://github.com/midona-rhel/Poser/issues/645) | Not started |
| W10 | [#646](https://github.com/midona-rhel/Poser/issues/646) | Not started |
| W11 | [#647](https://github.com/midona-rhel/Poser/issues/647) | Not started |
| W12 | [#648](https://github.com/midona-rhel/Poser/issues/648) | Not started |
| W13 | [#649](https://github.com/midona-rhel/Poser/issues/649) | Not started |
| W14 | [#650](https://github.com/midona-rhel/Poser/issues/650) | Not started |
| W15 | [#651](https://github.com/midona-rhel/Poser/issues/651) | Not started |

Track each package under #610 and create/link a small implementation issue for each concrete slice before coding it. Use #631–#637 where already applicable rather than duplicating them. The area evidence remains linked below; this document is the single source of implementation order.

Each slice records: current owner/callers, proposed owner, actual code to remove, supported behavior preserved or intentionally changed, guard decisions, verification and acceptance, and the commit that removed the old route. Its status is **not started**, **tracing**, **implementing**, **review/testing**, or **complete**. An unresolved evidence question is stated explicitly, not reported as a completed fix. At this plan revision, all W01–W15 are planned or continuing review; none is declared runtime-complete by the documentation audit.

The program is complete only when:

- Every actor-applied state area and scene-wide behavior has an identifiable state, command and cleanup owner. The user/developer can inspect what Poser has applied without reconstructing it from unrelated dictionaries.
- Every work package has either delivered its supported simplifications or documented why the existing implementation already meets the target. “A service exists” is not that evidence.
- Competing implementations, obsolete stores/registrations and transitional wrappers have been removed. There is no permanent old/new system running side by side.
- Every source comment meets the readability review, and normative documentation explains the current code in ordinary language. File coverage alone does not close W01.
- Guards and recovery branches have been assessed for reachable behavior; pointless scenarios and their code/tests are removed, while actual native and asynchronous lifetime requirements remain.
- Supported file formats, normal workflows and undo behavior are preserved unless a separately accepted change says otherwise. Relevant Release and user acceptance evidence is recorded without repeating unrelated tests.

This is a full-application program delivered as small changes. It is not a promise that every service needs rewriting, and it is not complete after the first handful of bug fixes.

## Further findings and evidence

The area plans supply evidence for the packages above. Their findings are mapped into the program, but are not all mandatory tests for every individual change:
[Domain](audit-2026-10-25/domain-plan.md), [Game](audit-2026-10-25/game-plan.md),
[Application](audit-2026-10-25/application-plan.md), [Documents](audit-2026-10-25/documents-plan.md),
[UI/host](audit-2026-10-25/ui-host-plan.md), [scene/world](audit-2026-10-25/game-extra-plan.md),
[spawn](audit-2026-10-25/game-spawn-plan.md), [gaze](audit-2026-10-25/gaze-plan.md),
[tests/build](audit-2026-10-25/domain-tests-scripts-build-plan.md), and
[generated-source limitations](audit-2026-10-25/generated-review-plan.md).

Before implementing a suspected timing problem, establish a reachable caller and outcome. Do not promote hypothetical fast-click, malformed-input or concurrency stories to requirements without that evidence. Preserve deliberate differences such as owned-object destruction versus borrowed-object restoration.

The documentation pass in `3b543112` changed 504 C# files with no executable-token/directive differences in normal or DEBUG parsing. Release compilation and 911 existing tests passed. That validates the patch, not the correctness of every behavior described in the audit. [#633](https://github.com/midona-rhel/Poser/issues/633) remains the readability work across these steps; comments must be reviewed for ordinary human comprehension, not just factual consistency.

### Clothing-only slice #652 - implementation checks

Requests now carry their actor identity; the existing post-publication scene
refresh cancels work for removed actors and retires body markers. GPose exit
uses the same retirement route. Native slot release is unchanged. Temporary
material cleanup failures are reported without retaining retry state.
Release build and all 913 existing tests passed before deployment. The existing
cancellation test now also covers actor removal without cancelling another
actor's request: 0 added, 0 removed, net 0 test cases. Live acceptance remains
apply/redraw, removal, GPose re-entry and scene save/load of clothing-only.
The broader asynchronous shutdown ordering work remains under #436.

### Evidence correction: Penumbra cleanup

Inspected Penumbra commit ce54aa5d: TemporaryApi.RemoveTemporaryMod delegates
synchronously to TempModManager.Unregister, whose normal results are Success
and NotRegistered. Collection deletion removes its temporary-mod records.
No demonstrated failure leaves an override requiring a later retry. Removed
the speculative collection retry store, retry loop and helper added in #652.
Keep finally-based removal, missing-collection success and unexpected-error
reporting. API error categories alone do not justify recovery machinery.

### Actor transform slice #631 - implementation checks

Confirmed the conflicting scale clamp by tracing TransformRuntimePort through
PosingService to DrawObject.Object.Scale. The domain accepts signed and small
scales; this writer replaced them with values in 0.01-100. Brio's
Game/Posing/ModelTransformService writes the supplied transform directly, with
no corresponding clamp. Removed the second range policy, preserving finite
value and rotation validation. This is not a claim that every finite scale is
supported by every downstream calculation.

SetTransformOverride now returns the actual native write result. Parenting,
transform apply/restore, scene placement and position lock use it. Removed
post-write checks that only established whether any older override existed.
Release build and all 913 existing tests pass. No unit tests added or removed;
native scale/readback and UI acceptance remain live checks.

Live verification on 2026-10-25, deployed probe head `409edac9`: ran
`tools/live_actor_scale_check.py` against actor 0 through the normal transform
facade. All 12 checks passed: 0.005, 0.000001 and negative X scale each matched
both stored and native draw-object readback after apply and redo; undo restored
the original scale. The actor ended at (1, 1, 1). Evidence:
[audit-2026-10-25/631-live-scale.json](audit-2026-10-25/631-live-scale.json).
This verifies native scale/history behavior; it does not claim UI acceptance.

### Transform conversion #632 - rendered IK frame slice

At `4a6aca5a`, Skeleton exposes the renderer's existing position, rotation and
scale before matrix construction. Four IK conversions now use that rotation
directly instead of attempting to recover it from a scaled matrix. Position
conversion still uses the matrix and requires an inverse where appropriate.
No replacement decomposition algorithm or fallback was added. Net six
production lines removed, excluding Debug probe instrumentation.

Release build and 913 existing tests passed; no unit cases added or removed.
Ran `tools/live_ik_frame_check.py` on the deployed candidate: FABRIK and Rope,
world target with end rotation, actor rotated 45 degrees, scales (1,1,1),
(0.005,0.005,0.005), and (-1,1.5,0.75). All six comparisons passed; maximum
world-target versus renderer-composed hand rotation error was below 0.00002
degrees. Restored actor scale/rotation and disabled test IK. See
[audit-2026-10-25/632-live-ik-frame.json](audit-2026-10-25/632-live-ik-frame.json).

#632 remains open: FromMatrix is still used by reference-pose composition,
BoneWorld's cached/live composition, and collider parent-frame capture. Those
combine transforms and can introduce shear; this slice does not claim to fix
them or to validate every TwoJoint/bone-target interaction.

### Inactive pose autosave writer #635

Sol traced the production callers and confirmed that the old Tick,
CaptureForExit and CompleteForExit methods were no longer called. Removed the
old capture adapter, scheduler, queue, writer, health ledger and pose store.
The existing AutoSaveLibrary reads the configured legacy root. Old status
records no longer produce a persistent recovery warning. SceneAutoSaveService and its runtime/store remain
unchanged. Existing backup files are not deleted by this change.

Removed five tests of the deleted scheduler, pose snapshots and pose retention,
plus their harness. Scene-autosave and legacy-reader tests remain. Added zero
unit tests; net minus five, with all 908 remaining tests passing. Release build
passed. Read-only Sol reviews also covered the preceding clothing, transform
and IK changes; their supported scan removal and comment corrections are in
7eaf1e37. The initial review led to preserving a legacy recovery warning without a
justified user action. That decision was corrected: the warning, status store
and its two tests were removed. Existing files remain untouched.
