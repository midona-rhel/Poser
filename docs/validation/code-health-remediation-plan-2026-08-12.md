
# Poser master greenfield migration and feature plan

Date: 2026-08-12. Revision input: 2026-08-13. Status: Review (planning
candidate; this plan status remains Review). This is the single dated,
non-normative execution plan for the code-health and feature audits. Durable
contracts remain in the normative homes indexed by [docs/README.md](../README.md).

This candidate changes only this plan. The sole UI-lab/tombstone candidate is
the complete chain `727ccb7 -> cb86af7 ->
3ea67f5100ea6808a67f8dcf7d0ab3d22f9f86ea ->
9de84646a5f2d4c84c9609069d92b87100556300 ->
c7d2c2e44bd008896d84f05cd716f75bcc7464f4`, integrated at organizer-accepted
head `cdf306e1c946a0af09ddd2c55ab83be0c2449ba9`. The organizer accepted that
head after independent no-findings review and Release build/tests (175/175,
no Debug/live); the actual-in-game-only UI visual policy is now in force. The
chain owns
`tools/ui-conformance/**`, `docs/process/testing.md`,
`docs/architecture/ui-workspace.md`, and the PBI-011/014/015/015A/016
dispositions/tombstones. This plan records the accepted candidate and
execution contract; it does not edit those paths or create a second owner.

## TL;DR

Migrate one useful vertical feature at a time through the existing compiler
real layers. Keep cohesive concrete owners and the native algorithms that
already preserve identity, ordering, and failure behavior. Share only small
policies with real cross-cutting contracts: typed outcomes, operation receipts,
atomic writes, and explicit lifecycle phases. The end state removes PosingCore,
but it does not begin with a wholesale rewrite or introduce a generic manager,
service, mediator, capability-bag, repository, or interface-per-class layer.

The accepted starting point is Train 1.1 at
dd055101115c4c60a361587fcff4c7bee2f72108
(poser-train-1-1-accepted-2026-08-12). Slice 0 records that baseline and does
not redo it. The corrected dependency order is:

~~~text
0 accepted T1.1
  -> 1 contract repair and dependency freeze
  -> 2 mutation outcomes and recovery
  -> 3 one session lifecycle coordinator and native startup health
  -> 4 operation epochs and receipt values
  -> 5 exact bindings, spawn handles, and relationships
  -> 6 pose transaction/materialization strangler
  -> 7 portable pose, persistence, and recovery stores
  -> 8 async materialization and MCDF/integration transactions
  -> 9 animation, presentation, camera, light, and environment
  -> 10 actual in-game UI surfaces and read models
  -> 11 whole-shot and remaining product verticals
  -> 12 PosingCore/facade/EventBus deletion and assembly enforcement
~~~

The order deliberately differs from the old Train 1–8 sequence. Autosave's
final capture is ordered before operation invalidation; invalidation precedes
restoration and native destruction. Receipt epochs are established before any
async caller is migrated. Exact binding ownership precedes pose materialization.
Portable identity and storage validation precede async import and MCDF file
work. Structural extraction starts immediately after the local contract test
for each owner; only final deletion and whole-graph enforcement wait for the
last proof.

## Current checked baseline

The source claims below were checked against HEAD at the accepted T1.1 tag;
they are observations, not new architecture contracts.

| Checked fact | Evidence and consequence |
|---|---|
| T1.1 is the accepted base | git show dd055101... is tagged poser-train-1-1-accepted-2026-08-12; its three changes harden Poser.ContractTests characterization for activation, transaction rollback, and exact replacement-generation refusal. No production code changed in that commit. |
| The current solution is transitional | Poser.slnx still contains Poser.UI, host Poser, Poser.Application, Poser.Domain, Poser.Game, PosingCore, PosingCore.Tests, and Poser.ContractTests. The final Poser.Domain.Tests and Poser.Application.Tests targets do not yet exist; T1.1 is transitional Poser.ContractTests coverage. |
| Current compiler direction is sound but incomplete | Poser.Domain has no project references; Poser.Application references Domain; Poser.Game references Domain, Application, and PosingCore; host Poser references all of those plus UI. The migration preserves this direction while removing the PosingCore edge. |
| Runtime/native quarantine is not yet compiler-real | PosingCore still permits unsafe code and contains native entity/file/runtime code; Poser.Game still references it. Poser.UI is currently the Crystarium kernel project, while product UI under Poser/UI is in the host. Poser/UI/Panes/GraphicalBonePane.cs still imports native client structs. |
| Native failure is inconsistent | Gaze construction is an eager, signature/hook failure path; IK and bone-hook capability loss can be silent. Current contract tests cover application seams, not the full native lifecycle. |
| Dated gate evidence is not final acceptance | The audit snapshot reports 166 Release tests passing and a successful Release build with three warnings. Treat that as historical evidence to reverify at each accepted head; warnings must be removed or explicitly accepted by the organizer. |
| The UI visual lab is transitional tooling | The integrated chain named above deleted `tools/ui-conformance` and owns the related UI/testing/PBI paths. The organizer accepted integrated head `cdf306e` after independent no-findings review and Release build/tests (175/175, no Debug/live). The actual in-game UI is now the sole visual oracle; no replacement lab or synthetic visual gate is authorized. |

## IK bake safety hold

The current live-gate artifact has AcceptanceQualified=false and Release
remains unqualified. The independent adjudication verdict is **INSUFFICIENT
EVIDENCE PENDING INSTRUMENTATION**. Diagnosis A strongly explains the observed
eight harness failures: the harness captures absolute T0, applies no animation
pause, waits frames, and compares raw transforms/direct CreatePoseFile output
to historical absolutes over a moving baseline. Diagnosis B remains a real,
unproven production risk: there is no explicit separate post-disarm settle,
and the identity/Written/history inclusion path can omit a target without a
prior interactive override. Treat Bake IK as unsafe and unqualified meanwhile:

- no harness fix or production Bake IK fix is authorized; only the additive,
  diagnostic-only harness path explicitly scoped in 4D is allowed;
- no slice may turn either diagnosis into a normative architecture rule;
- no /poser test posing.ik-bake result is a passing gate for this program;
- do not prescribe a harness fix or production fix and do not change the
  normative Bake contract yet;
- generic receipt/epoch contracts may be characterized with fakes, but the IK
  bake caller, production semantics, and behavior remain excluded until
  instrumentation produces sufficient evidence. The 4D diagnostic path is not
  a harness or production fix and cannot change the Bake contract.

Before any production IK rewrite, schedule a diagnostic-only IK qualification
tranche. It may add an additive diagnostic-only harness path in
`LiveTestService` (or a separately named diagnostic scenario) and structured
logging, but it may not change Bake semantics, history, settle timing, or
acceptance rules. Both pre- and post-state exports must go through the existing
production `CleanPoseFacade`/`PoseExportCapture` capture API, never direct
`CreatePoseFile`, and completion must carry the exact actor/session generation
and operation receipt. Recreate/reset the identical controlled actor
generation, or deterministically restore the same raw, interactive-stack,
history, IK, target, witness, and animation baseline before each arm; assert
baseline equality before interpretation, otherwise the result remains
insufficient. Use the same actor, chain, and witness for controlled A/B cases
with animation running versus explicitly paused/speed 0. Record animation
timeline/speed/control state; desired, basis, delta, identity, Written, order,
and history targets and stack counts; immediate post-complete state; undo/redo
stack state; and the production PoseExportCapture result. The tranche reports
evidence only; Release stays unqualified and Bake IK remains unsafe until the
organizer authorizes a separate production contract.

## Target architecture and ownership

The target direction below is a Review proposal, not an already changed
normative contract. Slice 1 implementation cannot begin until its normative
reconciliation prerequisite is accepted; this non-normative plan is not
evidence that the current normative homes have already been updated.

### Minimal compiler-real assembly graph

The target has exactly these product assemblies, with Persistence conditional:

~~~text
Poser.Domain       (no project references; pure identities, math, policies)
        ^
        |
Poser.Application  (Domain; logical session, scene, actions, transactions,
                    outcomes, history, read models, and narrow storage ports)
        ^                 ^                    ^                 ^
        |                 |                    |                 |
Poser.Game              Poser.Persistence     Poser.UI           Host: Poser
(Domain + Application)  (Domain + minimum     (Application +     (composes all)
                        Application storage   current UI kernel
                        contracts only)      + product assets)
~~~

Poser.Game keeps its current project and name and is the sole native/runtime
assembly. It owns unsafe code, pointers, signatures, hooks, native index hints,
framework-thread checks, and native handle revalidation. It is not renamed to
Poser.Runtime.

Poser.Persistence is created only when a compile/test proof shows it is
host-free: it may depend on Domain and the minimum Application storage
contracts, but never on Game, Dalamud, ImGui, native state, or live UI state. If
that proof cannot hold, the implementation stays in Application behind the
same narrow storage contracts; no project is added for cosmetic separation.

There is no Poser.UI.Kernel project. The current Poser.UI project already
serves as the kernel assembly through its rendering/primitives/compositions
folders and namespaces. Product surfaces and product assets can move into that
same assembly in vertical slices once they consume typed Application read
models/actions; a separate kernel assembly is justified only by a demonstrated
independent compile boundary, not by naming. The host keeps composition and
lifecycle wiring only. UI has no reference to Game or native entities.

Tests and tools follow the narrowest layer they exercise. PosingCore.Tests
may be migrated with a feature, but it is not a reason to retain PosingCore.
The exact UI-lab/tombstone chain named at the top owns the disjoint lab and
related documentation/PBI cleanup. Its actual-in-game-only policy is operative
at integrated head `cdf306e`; it is not replaced by another capture, browser,
golden, component, or pixel lab.

### Concrete end-state owners

| Contract or state | Sole owner and boundary |
|---|---|
| Logical scene and generation-qualified read state | Application SceneStore, the one owner to which current SceneSession converges. It owns the pointer-free SceneSnapshot, revision, exact-generation reconciliation, and read models. No second scene store. |
| Native binding | Game BindingRegistry, the current StableBindingRegistry boundary after its callers are proven. It maps exact IDs to native handles and is the only place that resolves/revalidates native identity. Pointers and native addresses never escape. |
| Selection | Application owns stable-ID selection rules and reconciliation; UI owns each surface's scope lifetime and ephemeral presentation state. A typed SelectionScope is explicit per surface, so pop-outs do not share singleton pane state and actions never read hidden ambient targets. |
| Mutation and rollback | Application MutationCoordinator owns one mutation contract. ChangeOutcome is a small typed value with primary result, rollback attempt/failures, history publication, and recovery requirement. A rollback ledger is private to the coordinator call; there is no generic operation framework. |
| Session lifecycle | One Application SessionLifecycleCoordinator owns startup, quiesce, and exit phase state. The phases are explicit: reserve/capture the final snapshot, invalidate operation epochs, restore Poser-owned state, destroy native entities, clear bindings, then publish read-model/UI notification. Host invokes it; it does not create one coordinator per feature. |
| Async result | OperationReceipt is an immutable value/read model with session generation and operation state/outcome. Pending is a non-terminal acknowledgement; Applied, RolledBack, Failed, RecoveryRequired, and Cancelled are terminal outcomes. The owning workflow retains its active epoch; there is no standalone registry/mediator abstraction. |
| Persistence | Host-free Persistence owns versioned codecs, finite-value validation, atomic same-directory writes, autosave queue/join, library index, and quarantine/recovery records. Application owns only narrow storage contracts and recovery/read-model semantics. |
| MCDF | Application McdfTransaction owns the workflow, phases, receipt, cancellation, and rollback. Persistence owns file/codec policy; Game owns vendor/native integration and redraw completion. External appearance systems remain their owners. |
| Spawn and relationships | Game SpawnHandle owns native address/index/creation-serial revalidation; Application owns logical spawn/relationship ownership and stable parent/bone identities. Failed deletion remains owned for retry. Companion variants are explicit workflows, not hidden polling. |
| Animation/presentation/scene objects | Existing cohesive Application sessions and Game ports remain owners. Physics patching is a local Game owner after its byte/layout contract is tested. Camera/light/environment and object relationships are not folded into a generic scene manager. |
| UI | Actual UI surfaces own filter/disclosure/hover/picker/dialog and per-surface state, and invoke typed Application actions. They consume Application read models and never reference Game, native entities, or native baselines. |
| Host | Composition, dependency wiring, plugin lifetime, and calls into SessionLifecycleCoordinator only. It does not author product state or repair production behavior. |

### Preserved product and runtime invariants

- Actor, skeleton, slot, and bone IDs remain exact-generation identities;
  slot-qualified bones never fall back by name or across slots.
- Domain math and policies remain pointer-free and pure. Native BoneIndex is
  only a lookup hint and mismatch guard, never the sole portable identity.
- Gesture baselines are captured once and frozen; updates use total deltas;
  one committed edit yields one history patch; undo/redo use the same restore
  path as cancel.
- Named expression/gaze/runtime producer layers are not manual history. Normal
  reset/history preserves them; explicit Reset All remains the exception.
- Transform propagation accepts every valid subset of All, including None;
  unknown bits fail typed at the boundary. Fixed supported IK policy is
  preserved until the diagnostic-only qualification tranche and a new contract
  authorize change.
- Portable pose entries are ordered and structurally identified by slot,
  partial, canonical name, and a BonePath/parent path where needed. Ambiguous
  matches are explicit failures or user choices. Legacy duplicate-name files
  cannot silently overwrite or broadcast.
- Native ordering remains game animation/IK/physics, then Poser persistent
  layers, with the existing cache/reparent/cache/finalize sequence preserved
  until a characterization and live parity proof permits a change. Brio/Ktisis
  references explain compatibility decisions only; they are not replacement
  architectures.
- Poser restores only the presentation, native, integration, and scene state
  it owns. Glamourer/Penumbra/Customize+ retain equipment, customization,
  dyes, materials, and saved-design ownership. Poser owns Model ID, supported
  presentation values, posing, scene objects/relationships, playback, MCDF,
  and recovery.
- There is no animation-authoring timeline. Playback, stance, scrubbing,
  expression, gaze, and physics controls remain useful features.
- At integrated head `cdf306e`, actual in-game Poser UI is the only visual
  oracle. The synthetic component/Picto/browser/golden/capture lab was deleted
  by that sole chain owner and is not replaced.

### Explicitly rejected abstractions

Do not add a manager/coordinator/service/repository per noun, a generic
repository, mediator, service locator, capability bag, interface-per-class
mirror, inheritance hierarchy, event-order lifecycle, or broad I*Service
facade. Interfaces belong at real runtime/storage seams. Application sessions
remain concrete. A shared type is justified only when it owns a cross-cutting
invariant and has a fakeable contract: outcome, receipt, atomic writer,
storage port, or lifecycle phase.

## Program operating contract

### Ownership and review

Each candidate below is a separate Luna implementation tranche with one sole
writer for the listed mutable owner and an exact allowed-path set. No two
implementation chats edit a shared owner concurrently. The organizer writes
specifications, controls scope, runs authoritative Release gates, records
finding disposition, and accepts or rejects the head. The organizer does not
author or repair production code.

Before implementation, the organizer records the immutable accepted base SHA,
branch/worktree, Luna owner, one behavior contract, allowed paths, exclusions,
preserved invariants, rollback seam, and gate. The implementation chat adds
characterization/ordinary tests first, then the smallest behavior or move that
the contract permits. Structural extraction starts immediately after that
local contract is green; it is not postponed to a wholesale final rewrite.

Every candidate ends in Review, not Accepted, until the organizer records
the exact head and evidence. An independent reviewer inspects the exact
BASE..CANDIDATE range read-only. Accepted findings return to the same Luna
writer as new commits; the affected reviewer rechecks the fix range, then an
independent reviewer rechecks the complete original-base-to-new-head range.
The organizer records accepted, rejected-with-rationale, or deferred findings.
Build/test failures return to the implementation chat; the organizer never
patches the candidate.

Every Luna task begins ongoing and final handoffs with a short TL;DR and sends
its complete blocker or final report directly to the organizer task through
task messaging before ending. A child-task final answer alone is insufficient.

### Release, Debug, and live gates

The organizer owns these non-deployment gates at each applicable candidate:

~~~powershell
dotnet build Poser.slnx -c Release --no-restore --nologo
dotnet test Poser.slnx -c Release --no-restore --nologo
~~~

Release fixtures/fakes are mandatory for corrupt data, rollback failure,
invalid opcodes/layouts, unavailable native capabilities, late callbacks, and
external-plugin absence. A Debug build auto-deploys to the live game and is
never used for ordinary compilation, tests, fault injection, or review. It is
required only after the exact reviewed head passes its Release gates and the
organizer announces the deployment. Live evidence uses the smallest applicable
scenario, persisted run.json, and the external reader; AcceptanceQualified is
required only where the acceptance card explicitly calls for it. Not
exercised is evidence of scope, never a pass.

The retained in-game scenario IDs are selection.actor-bone-clear,
transform.actor-components, transform.actor-undo-redo, posing.bone-components,
posing.animation-interference, posing.reset-region, posing.copy-paste-pose, and
posing.ik-bake. Use the narrowest applicable scenario for a slice; reserve
/poser test full for the accepted baseline, GPose-exit or harness changes,
final program acceptance, or a failed/ambiguous focused gate. The IK-bake ID is
on the safety hold above and cannot pass this program. Validate persisted
results with tools/Test-PoserLiveRun.ps1; chat text or file existence is not a
verdict.

Tranche state is Planned -> Spec ready -> Implementing -> Review -> Rework ->
Automated pass -> Acceptance pending -> Accepted. Exceptional states are
Blocked, Reverted, Superseded, and Parked, each with an owner and reason.
Complete means Accepted, not compiled, deployed, or apparently working.
Native/lifecycle/async/exit/spawn/MCDF/patch/ownership work uses Luna Max;
docs, pure tests, mechanical moves, and small UI changes may use the smaller
Luna efforts. The organizer records the state and effort in every handoff.

### Shared evidence shape

Each slice's evidence must include: exact base/head and changed paths; local
characterization/ordinary test results; organizer Release commands/results and
warnings; independent review ranges and finding dispositions; rollback-seam
exercise; live card or Debug: N/A reason; artifact identity; and the next
accepted owner. The exact cards below are the minimum, not permission to widen
scope.

## Slice 0 — record the accepted T1.1 foundation

**Owner / writer:** organizer records status only; no implementation writer.

**Allowed:** this plan's baseline entry and the accepted T1.1 evidence. **Excluded:**
all source, tests, project files, tooling, builds, deployment, and a second
contract-test foundation.

**Contract and preserved invariants:** treat
dd055101115c4c60a361587fcff4c7bee2f72108 as Accepted. Keep its activation
fakes, exact-generation replacement refusal, transaction rollback
characterization, and current compiler direction as the starting seam. Do not
claim that T1.1 proves native lifecycle or async safety.

**Tests first:** no rerun is required by this planning task; future slices
extend the accepted tests rather than redoing the foundation.

**Release / Debug:** Release: N/A for this record-only slice. Debug: N/A.

**Review / rework:** independent exact-range review remains required for any
future T1.1 documentation/status fix; there is no implementation rework in
this record-only slice.

**Rollback seam:** Git history at the immutable tag is the rollback seam.

**Review / completion evidence:** the tag, SHA, changed-path list, and the
fact that this program begins at T1.1 rather than redoing it.

### Prerequisite 1A — normative reconciliation before Slice 1 implementation

**State owner / sole writer:** one existing-normative-home documentation Luna
writer, before the Slice 1 implementation writer starts. The exact
UI-lab/tombstone chain named at the top remains the sole owner of
`docs/process/testing.md`, `docs/architecture/ui-workspace.md`, and its
PBI-011/014/015/015A/016 dispositions/tombstones; this prerequisite links to
that chain and does not duplicate or edit its paths.

**Allowed:** the existing normative homes
`docs/architecture/product-and-boundaries.md`,
`docs/architecture/application-state.md`,
`docs/architecture/posing-runtime.md`, and
`docs/features/files-and-transfer.md`; `docs/README.md` only for index/link
hygiene; and the exact UI-lab/tombstone chain's owned UI/testing paths when
that chain performs its own update. **Excluded:** source, tests, project
files, a second migration plan, and a document per class or interface.

**Reconciliation contract:** the accepted diff must reconcile the following
source-backed collisions in the existing normative homes. The master plan is
only the assignment and evidence index; it does not satisfy R6 itself.

| Required reconciliation | Current role/traversal to record | Durable home and exit evidence |
|---|---|---|
| PosingCore versus LegacyRuntime roles | `PosingCore/PosingCore.csproj` currently supplies `Poser.Core`, `Poser.Entities`, `Poser.Services`, `Poser.Files`, `Poser.Library`, `Poser.Config`, and `Poser.Game` namespaces. Its `Entities/ActorBase.cs`, `Skeleton.cs`, `Bone.cs`, `Core/NativeHelpers.cs`, and `Game/ExpressionService.cs` still cross live/native boundaries, while `Poser.Game/LegacyRuntime` contains the concrete `Poser.Game` services. | `product-and-boundaries.md` assigns each current area to Domain, Application, host-free Persistence, Game, UI/product assets, or Host; `posing-runtime.md` keeps all unsafe/native access in Game. Evidence maps every current owner and caller before/after. |
| PosingCore area classification | `Core/PoseMath` and bone metadata are pure candidates; `Entities`/`NativeHelpers` are live/native candidates; `Files`/`Library`/`AutoSave` are codec, index, and storage candidates; `Services` are transitional contracts; `Config` and embedded UI/data assets are product/configuration candidates. | `product-and-boundaries.md` records Domain for pure math/policies, Application for logical state/actions and narrow ports, host-free Persistence for codecs/stores when proven, Game for live entities/native adapters, UI/product assets for surface state/assets, and Host for composition. `files-and-transfer.md` owns file/autosave terminology; `posing-runtime.md` owns native ordering and ports. |
| Features and native writes crossing the split | Actor/GPose discovery and exit (`ActorManager`, `GPoseService`); skeleton/slot/bone reads and apply hooks (`SkeletonService`, `SlotCharacterBases`, `BonePosingService`); IK/gaze/position overrides (`IKService`, `GazeService`, `PosingService`, `TransformRuntimePort`); actor/companion/prop/model/visibility spawn (`ActorSpawnService`, `PropSpawnService`); pose import/export, file/library/autosave, animation, presentation, camera, light, environment, and MCDF paths all currently cross old `Poser.Services`/entity contracts. | `posing-runtime.md` names `TransformRuntimePort` as the one native write path and records the remaining Game ports/hooks; `product-and-boundaries.md` records the feature owner and external-appearance boundary; `files-and-transfer.md` records storage/autosave ownership. Evidence is caller search plus compiler-real dependency proof, not a namespace rename. |
| Target naming and interface policy | Current docs still say `Poser.Core` and `Poser.Runtime`, while the project is `PosingCore` and the native project is `Poser.Game`. | `product-and-boundaries.md` names Domain, Application, current `Poser.Game`, conditional host-free Persistence, current Poser.UI, and Host; `posing-runtime.md` names the native boundary. Keep `Poser.Game`, do not add `Poser.Runtime` or `Poser.UI.Kernel`, and retain concrete owners with interfaces only at real runtime/storage seams. |
| LegacyRuntime exit/deletion criteria | `Poser.Game/LegacyRuntime` is a folder whose classes use the `Poser.Game` namespace; it is not a target assembly boundary. | `posing-runtime.md` requires characterization/ordinary tests, accepted caller migration, exact-generation and lifecycle proof, no remaining PosingCore edge, and no native write outside Game before each LegacyRuntime owner/file is removed. Failed cleanup remains recoverable; no wholesale folder deletion. |
| Namespace quirks | `PosingCore.csproj` has RootNamespace `Poser`; `Poser.Game/LegacyRuntime/*.cs` declares `namespace Poser.Game`; `Poser.Core`, `Poser.Entities`, `Poser.Files`, and `Poser.Services` therefore do not identify separate assemblies. | `product-and-boundaries.md` is the terminology/glossary home; `posing-runtime.md` records the actual assembly/native boundary and aliases. Evidence includes namespace-to-project and project-reference checks. |
| Terminology collision | “PosingCore”, “Poser.Core”, “Poser.Domain”, stale “Poser.Runtime”, and the `LegacyRuntime` folder are currently easy to conflate. | Reconcile the terms in the two existing homes and link from other docs. A concise `backend-migration-state` home may be proposed only if the writer proves no existing home fits; it must not become a per-class document or a substitute for this plan's non-normative assignment. |

The same accepted diff must reconcile `files-and-transfer.md` event-order/
autosave wording with the explicit SessionLifecycleCoordinator order (final
autosave capture, operation invalidation, restoration, destruction), and
`application-state.md`/`posing-runtime.md` stable identity, exact binding,
operation receipt/epoch, selection, and lifecycle contracts. The writer must
record every other named stale normative home found by the contradiction sweep,
assign it to an existing home, and link rather than duplicate prose. The UI
actual-in-game-only policy is operative at integrated head `cdf306e`.

**Tests/review/gates:** perform path/link and contradiction checks first, then
independent exact-range review and rework. Evidence is the accepted normative
diff set for the named homes, the owner/traversal matrix above, a contradiction
report showing every stale claim's disposition, compiler-real project/namespace
proof, and the accepted UI-lab/tombstone-chain head when applicable. Release:
N/A for docs-only work; Debug: N/A. **Rollback seam:** the prior accepted
normative heads and pre-reconciliation links remain recoverable in Git. The
target direction remains a Review proposal until this prerequisite is
accepted; no Slice 1 implementation may claim that stale normative docs are
already changed, and no migration-state document is accepted merely because
this plan names it.

## Slice 1 — contract repair, dependency freeze, and pure Domain corrections

**State owner / sole Luna writer:** one contract-and-Domain Luna writer. A
separate UI-surface writer may later own product-surface migrations, but the
exact UI-lab/tombstone chain named above is the only owner of the lab,
UI/testing, and listed PBI cleanup.

**Allowed:** Poser.Domain/**; narrow Application contract/state paths under
Poser.Application/Scene, Poser.Application/Selection, and explicit storage
contracts; transitional `Poser.ContractTests/**` with its Application coverage
retained in place; creation/migration of the final `Poser.Domain.Tests/**`
target and only its `Poser.slnx` project entry/reference;
project-reference/dependency checks; and source/reference tombstones needed to
make this plan authoritative.
**Excluded:** runtime/native behavior, PosingCore deletion, UI surface
rewrites, `Poser.Application.Tests/**` and its project entry, IK bake behavior,
broad backlog prose, and any generic framework.

**Non-overlapping candidates:**

1. Freeze the six-assembly target graph, the Poser.Game runtime boundary,
   conditional host-free Persistence rule, and the no-separate-kernel decision.
2. Create/migrate only the final `Poser.Domain.Tests` target from the
   transitional T1.1 coverage. Retain transitional Application coverage in
   `Poser.ContractTests`; Slice 2 exclusively creates/migrates
   `Poser.Application.Tests` and owns its Application families. Later owner
   slices add their own final test families; this item is not complete at
   Slice 0.
3. Correct stable IDs, SceneStore/SelectionScope contracts, complete
   SceneSnapshot fields needed for relationships/ownership, and fixed IK
   policy. Converge SceneSession to the one scene owner; do not create a
   parallel store.
4. Correct PoseLayer.None, finite/normalized pure transform policy, and typed
   unknown-mask failure.
5. Correct PortablePose structural identity: ordered entries, explicit
   BonePath, ambiguity reporting, and native index as a hint only. Preserve
   useful legacy format behavior through an explicit compatibility adapter.
6. Search-verified dead API/event/documentation tombstones may land here when
   they do not overlap an active owner. EventBus replacement/deletion itself
   remains Slice 12. The exact UI-lab/tombstone chain owns its cleanup and has
   no replacement.

**Tests first:** create/migrate `Poser.Domain.Tests` only while retaining the
accepted `Poser.ContractTests` characterization, including its transitional
Application coverage. Domain tests cover all eight propagation masks,
including None; unknown bits; exact slot/generation/bone identity; ordered
selection scopes; complete scene snapshot round-trip; portable duplicate,
ambiguous, path, and legacy-name fixtures; and fixed IK unsupported-endpoint
outcomes. Slice 2 owns migration of Application families. Tests must prove no
silent history or state mutation.

**Invariants:** pure Domain math; stable generation/slot identity; explicit
selection scopes; no raw address; no diagnosis or production fix for Bake IK;
no dropped/overwritten portable entries; current useful formats retained.

**Release / Debug:** organizer Release build/test and dependency-graph/source
checks. Debug: N/A; there is no native or visual behavior in this slice.

**Rollback seam:** current Domain/Application value types, SceneSession,
PortablePose constructor, and current file adapters remain callable until
the new contract tests and one consumer each are accepted.

**Review / completion evidence:** independent exact-range review and rework
loop; accepted `Poser.Domain.Tests` output, its project-graph proof, ambiguity
fixtures, and a mapping of every tombstoned caller. This tranche does not
claim acceptance of `Poser.Application.Tests`; the UI-lab/tombstone chain and
actual-in-game-only policy are already recorded at integrated head `cdf306e`.

## Slice 2 — Application mutation, outcome, and recovery kernel

**State owner / sole Luna writer:** one Application mutation writer owning
MutationCoordinator and its concrete callers.

**Allowed:** Poser.Application/Transforms/**, the cohesive mutation portions
of Poser.Application/Posing/**, new outcome/recovery records beside those
owners, creation/migration of `Poser.Application.Tests/**`, its `Poser.slnx`
project entry/reference, and the retained transitional
`Poser.ContractTests/**` cases while they are migrated. **Excluded:**
`Poser.Domain.Tests/**` ownership, native/runtime ports, Persistence
implementation, UI, EventBus replacement, async caller migration, and a
generic operation framework.

**Contract:** migrate discrete transform, gesture, and pose-edit paths to one
typed ChangeOutcome. The private per-call rollback ledger records every
capture/write/restore result. On rollback failure, the outcome carries the
primary and rollback failures, does not append history, marks recovery required,
and prevents a new write until retry or explicit stale-target disposal.

**Tests first:** exclusively create/migrate `Poser.Application.Tests` and move
the Application families out of their transitional `Poser.ContractTests`
coverage only after the final target proves the same contract. Add fake
ITransformRuntimePort tests for success, partial write, capture failure,
restore failure, stale target, unavailable capability, one history patch,
cancel, undo, redo, and recovery quarantine. Characterize existing native phase
ordering before changing any caller; `Poser.Domain.Tests` is owned by Slice 1.

**Invariants:** frozen baselines, total deltas, one patch, exact target
containment, no false success, and no shared rollback ledger or hidden async
success.

**Release / Debug:** organizer Release build/test; Debug: N/A because this
slice is Application/fake-port behavior and has no new native or visual seam.

**Rollback seam:** keep current TransformCommandService, PoseEditService,
and clean facades as adapters until each caller has the new outcome and its
ordinary/contract tests; remove an adapter only after the caller proof.

**Review / completion evidence:** exact-range independent review, rework and
full-range recheck; accepted `Poser.Application.Tests` output, migrated
Application-family inventory, outcome matrix, recovery record, history
evidence, and no new broad I*Service or mediator abstraction. This slice owns
the Application test target exclusively; it does not alter Domain test
ownership.

## Slice 3 — one SessionLifecycleCoordinator, startup rollback, and capability health

**State owner / sole Luna writer:** one lifecycle/native-startup Luna writer;
startup and lifecycle changes are sequential candidates under this owner, never
parallel edits to the same activation path.

**Allowed:** Poser.Application lifecycle contracts and
SessionLifecycleCoordinator; Poser/Poser.cs; Poser/Composition/**;
Poser.Game startup/native capability code including
LegacyRuntime/GazeService.cs, LegacyRuntime/IKService.cs, and bone-hook
activation; typed capability-health read models. **Excluded:** async
import/IK-bake caller behavior, Persistence implementation, UI surface
implementation, and EventBus ordering as lifecycle control.

**Contract:** one coordinator owns startup, quiesce, and exit. Final snapshot
capture is reserved first; operation epochs are invalidated second; Poser-owned
state is restored third; native actors/resources are destroyed fourth; bindings
are cleared fifth; only then are read models/UI notifications published. The
capture phase is a storage port/fake until Slice 7 wires the real autosave
coordinator. Startup construction is guarded so any activation failure disposes
already-created hooks/subscriptions/providers. Gaze, IK, and bone-hook failure
become explicit unavailable capability health, never silent success or whole
plugin orphaning.

**Tests first:** activation fakes fault every step and assert reverse cleanup;
ordered lifecycle fakes assert final-capture -> invalidation -> restore ->
destroy -> clear -> publish; repeated exit, late callback, restore failure,
and startup throw cases retain recovery. Native capability tests assert every
partial IK allocation is freed and unavailable bone apply refuses.

**Invariants:** no event-subscription ordering; no post-teardown publication;
no native pointer outside Game; no Bake IK behavior change; no recovery record
discarded.

**Release / Debug:** organizer Release fault tests/build. Debug: required for
the exact reviewed startup/native head only, with a focused card: reload in
GPose, confirm plugin remains loaded when optional Gaze is unavailable, confirm
visible capability health, exercise ordinary gaze when available, reload, and
report persisted evidence. Do not run the IK-bake scenario.

**Rollback seam:** current CleanSceneLifecycle, provider disposal, and
capability flags remain behind the coordinator ports until ordered fake tests
and the focused live card pass. EventBus notifications remain notifications,
not phase control.

**Review / completion evidence:** exact-range independent review/rework;
activation fault matrix, lifecycle order log, capability read model, Release
results, and the focused live artifact. The head ends Review pending organizer
acceptance.

## Slice 4 — operation epochs and receipt values

**State owner / sole Luna writer:** one Application operation-contract writer.

**Allowed:** immutable OperationReceipt/epoch value types and the small
active-operation state in MutationCoordinator/SessionLifecycleCoordinator;
Poser.ContractTests/**; read-model adapters needed to expose terminal state.
**Excluded:** migration of PoseImportCapture, IkBakeCapture, facial/import
callers, MCDF, UI panes, and any Bake IK harness or production fix.

**Contract:** every deferred workflow carries the exact session generation and
operation epoch from arm through terminal result. Invalidation happens before
rollback/restoration and makes every late callback a typed stale/cancelled
outcome. OperationReceipt is a value/read model, not a manager. The receipt
states are Pending, Applied, RolledBack, Failed, RecoveryRequired, and
Cancelled; no immediate schedule acknowledgement is presented as final
success.

**Tests first:** fake delayed callbacks for success, supersession, timeout,
cancel, actor replacement, session exit, late callback, rollback failure, and
receipt publication to the initiating surface/generation only. Test epoch
invalidation before restore/destroy.

**Invariants:** stale callbacks cannot write, no hidden async success, no
cross-generation read-model update, and no Bake IK diagnosis encoded.

**Release / Debug:** organizer Release tests/build only; Debug: N/A because
this is a contract/read-model slice and intentionally does not migrate an
async native caller.

**Rollback seam:** existing synchronous return values and capture state
machines remain adapters until one caller is migrated in Slice 8 with a
receipt and ordinary live evidence.

**Review / completion evidence:** exact-range independent review/rework, epoch
timeline tests, receipt-state table, and explicit confirmation that
IkBakeCapture, the existing Bake scenario semantics, and the disputed artifact
were untouched. If 4D is included in the reviewed range, its additive
diagnostic path is checked separately from those unchanged production
semantics.

### Diagnostic-only IK qualification tranche (4D; before any production rewrite)

**State owner / sole Luna writer:** organizer-owned diagnostic Luna writer;
this is instrumentation and evidence collection, not a production behavior
owner. It must run before any later slice is allowed to rewrite Bake IK.

**Allowed:** an additive diagnostic-only harness contract in
`Poser.Game/Validation/LiveTestService.cs` (or a separately named diagnostic
scenario in that validation area), its structured logging, and the existing
`LiveTestResult`/`LiveTestRunReport` persisted report path. The path may call
the existing production `CleanPoseFacade.CapturePoseFile` /
`PoseExportCapture` API, with no semantic change to that API; both its pre- and
post-state exports must use that capture boundary. Each completion must carry
and verify the exact actor logical ID and generation, session generation, and
diagnostic operation receipt/epoch; a mismatched callback is diagnostic
failure/insufficient evidence and cannot write the report. **Excluded:**
`IkBakeCapture` changes; changes to the existing Bake scenario's production
capture, semantics, verdict rules, or normative contract; direct
`CreatePoseFile` in the diagnostic path; settle/history/identity fixes;
unrelated harness contracts; acceptance-rule changes; and any production
rewrite based on either diagnosis.

**Contract / tests first:** before either A/B arm, recreate/reset the identical
controlled actor generation, or deterministically restore the same raw,
interactive-stack, history, IK, target, witness, and animation baseline. Log a
baseline fingerprint and assert equality across arms before interpretation; if
the actor generation or any required baseline field cannot be matched, the
outcome stays insufficient. Use the same actor, chain, and witness for
structured A/B cases with animation running versus explicitly paused/speed 0.
Log animation timeline, baseline, speed, and control state; desired, basis,
delta, identity, Written, order, and history targets and stack counts;
immediate post-complete state; undo/redo stack state; exact actor/session
generation and receipt; and both production `PoseExportCapture` results.
Validate both exports through `CleanPoseFacade.CapturePoseFile` /
`PoseExportCapture`, rather than direct `CreatePoseFile`. The diagnostic
output reports evidence only and never asserts a Bake semantic verdict.

**How evidence distinguishes the diagnoses:** Diagnosis A is supported when the
matched-baseline running case diverges while the explicitly paused/speed-0 case
converges, with the identity/Written/order/history targets and stack counts
otherwise equal; that demonstrates a moving animation baseline. Diagnosis B
remains supported if the matched paused/speed-0 case still omits a target or
mismatches, and the trace shows a missing identity/Written/history inclusion or
a separate post-disarm settle requirement. If baseline equality cannot be
asserted, both conditions occur, or the fields/receipt correlation are
incomplete, the result remains insufficient evidence and authorizes no
production change.

**Release / Debug:** Release remains unqualified for the current artifact.
Organizer-owned Release instrumentation/build evidence is required; Debug is
required only if the exact reviewed diagnostic head must observe the live game,
after the normal auto-deploy notice. The diagnostic result is not acceptance.

**Rollback seam / evidence:** diagnostics are additive and removable; retain
the pre-instrumentation accepted head and report both A/B traces, all requested
fields, immediate/undo/redo states, and the production PoseExportCapture trace.
The organizer may authorize a separate production contract only after this
evidence is independently reviewed. Until then Bake IK remains unsafe and
unqualified.

**Review / completion evidence:** independent exact-range diagnostic review and
rework, complete structured traces, explicit verdict on evidence sufficiency,
and no semantic production change.

## Slice 5 — exact bindings, spawn handles, and relationships

**State owner / sole Luna writer:** one identity/spawn Luna writer, with
sequential non-overlapping candidates for binding, spawn, companion, and
relationship ownership.

**Allowed:** Game binding/scene refresh paths (StableBindingRegistry and its
callers), LegacyRuntime/ActorSpawnService.cs, companion lifecycle paths,
Application scene/relationship state, Domain scene identity, and their
contract tests. **Excluded:** pose materialization, file codecs, MCDF,
animation, UI, and broad Runtime namespace cleanup.

**Contract:** the end-state BindingRegistry is the only native identity
boundary. SpawnHandle contains exact logical generation/address/creation
serial, with object-table index only as a lookup hint. Every classify,
companion poll, clone, and destroy revalidates the exact handle. Failed deletion
retains ownership for retry; every post-create failure rolls back the native
object. Application SceneStore owns stable relationship identities and
parent/bone contracts without addresses. Companion/minion/mount/ornament
workflows expose terminal status and child-side detach.

**Tests first:** slot reuse after foreign deletion; address/serial mismatch;
post-create failure; failed deletion retry; companion timeout/cancel/detach;
slot-specific skeleton replacement; relationship attach/detach/clone with
missing parent. Ordinary actor/prop/light/camera refresh tests prove one scene
owner and exact binding release.

**Invariants:** no unrelated replacement can be classified or destroyed;
exact slot generations; external appearance ownership remains external; no
detached worker retains a raw address.

**Release / Debug:** organizer Release tests/build and fault fixtures.
Debug: required for the exact reviewed native head, with a disposable card:
spawn/clone actor and prop, attach available companion variants, delete/reuse
the original slot through an external path, verify the replacement is never
owned or offered for despawn, then despawn/exit and report cleanup evidence.

**Rollback seam:** current index maps and spawn/companion callers remain behind
the new handle seam; old deletion is disabled only after exact revalidation and
failed-deletion tests prove ownership retention.

**Review / completion evidence:** exact-range review/rework, slot-reuse logs,
companion terminal receipts, relationship state, Release results, and the
focused live artifact. No pose or persistence files are changed in this slice.

## Slice 6 — pose transaction and materialization strangler

**State owner / sole Luna writer:** one pose-domain/runtime writer, with one
vertical user action per candidate. Do not assign a broad “rewrite PosingCore”
task.

**Allowed:** one selected feature at a time across Poser.Domain/Posing/**,
Poser.Application/Posing/** and Transforms/**, the matching Game runtime
port/adapter and one caller; Poser.ContractTests/** and focused existing
posing tests. **Excluded:** unrelated file/library formats, MCDF, UI-wide
rewrites, animation/presentation, EventBus deletion, and Bake IK behavior.

**Candidate order:** first migrate a normal reset-region/materialization path;
then mirror/flip/stash as separate candidates; then any selected/reference
action only after its own contract. Each candidate moves pure policy to Domain,
transaction/history to Application, and native materialization to Game. The
old CleanTransformFacade/CleanPoseFacade remains an adapter until every caller
of that feature is proven and deleted.

**Contract:** preserve game animation/IK/physics ordering, frozen raw baselines,
slot-qualified pose deltas, named-layer exclusion, total-delta gestures, one
patch, and explicit native refusal. Runtime is the only write path;
application commands accept stable IDs and revalidate through the binding port.

**Tests first:** ordinary and fake-port tests for each action's capture,
materialize, cancel, one-patch history, undo/redo, partial target failure,
unknown propagation bits, redraw replacement, and named-layer preservation.
Characterize the current native apply sequence before moving a caller.

**Release / Debug:** organizer Release tests/build for every candidate.
Debug: required for native posing candidates with a focused card: select one
actor/bone and each relevant slot, apply/reset/mirror as scoped, exercise cancel
and undo/redo, verify animation/expression layers remain correct, and report
the persisted live verdict. Never use posing.ik-bake.

**Rollback seam:** one old facade/caller path per feature remains selectable
until its exact-range review and live card pass; no simultaneous facade removal.

**Review / completion evidence:** local contract tests before extraction,
independent exact-range review/rework, native-order characterization, one
feature diff, Release output, live artifact, and proof that no broad PosingCore
rewrite was bundled.

## Slice 7 — portable pose, Persistence, autosave, library, and recovery

**State owner / sole Luna writer:** one Persistence/storage Luna writer, with
sequential codec, atomic-store, autosave, library, and recovery candidates.

**Allowed:** new Poser.Persistence/** only after the host-free dependency
proof; otherwise the same host-free code remains in its current layer behind
Application contracts; minimum Application storage contracts; the pure file,
codec, library, and autosave sources currently under PosingCore/Files/** and
PosingCore/Library/**; PosingCore.Tests/** format tests; disposable fixture
directories. **Excluded:** Game/Dalamud/ImGui/native state, UI implementation,
MCDF integration, EventBus, and project deletion.

**Contract:** versioned codecs validate finite numbers, quaternion norms, sizes,
versions, and bounded clipboard/library reads before planning writes. One
same-directory atomic writer serializes, flushes, replaces/moves, and preserves
the previous valid destination on failure. Library scan generations cancel and
bound traversal. Quarantine/recovery records retain corrupt/future/unwritable
state and last-success/error status. Autosave queues ordinary ticks but reserves
the final snapshot and joins/cancels workers before cleanup.

PortablePose codecs use ordered structural BonePath entries and explicit
ambiguity; native BoneIndex is only a hint. Unknown or duplicate legacy names
must produce a visible deterministic conflict, never last-write-wins or a
broadcast. Reset-before-import is derived from the successfully matched scope.

**Tests first:** compatibility fixtures for useful file formats (not visual UI);
invalid/non-finite/future/oversized inputs; alias collisions; atomic write
failure with old-file survival; autosave final-capture/worker-join races;
library cancellation/bounds; quarantine/recovery read models.

**Invariants:** format behavior is retained and made safer; no live native
state in Persistence; final capture occurs before lifecycle invalidation; no
fire-and-forget cleanup or silent recovery loss.

**Release / Debug:** organizer Release build/test and disposable storage
fixtures. Debug: N/A for host-free codecs and stores; user-visible recovery UI
is gated in Slice 10. No live file test may damage normal game state.

**Rollback seam:** old file mappers and AutoSave service remain behind the
Application storage contracts until each format/store has passed fixtures and
the organizer records old-file survival. Final worker wiring is joined by the
Slice 3 lifecycle contract, not by EventBus order.

**Review / completion evidence:** host-free dependency proof, codec/atomic/
autosave fixture output, exact-range review/rework, no Game reference, and
explicit list of retained/parked/rejected formats.

## Slice 8 — async materialization and integration/MCDF transactions

**State owner / sole Luna writer:** one async-materialization/integration Luna
writer. MCDF resource policy and pose-import materialization are sequential
non-overlapping candidates because they share redraw/cleanup timing but not
their owners.

**Allowed:** Poser.Game/Posing/PoseImportCapture.cs and the matching
Application action/read-model caller after Slice 4 epochs and Slice 7 codecs;
facial capture only with its own receipt; Poser.Application/Integration/**,
Poser.Game/Integration/**, IMcdfFileBoundary, and one concrete
McdfTransaction; required Game vendor ports; focused tests. **Excluded:**
IkBakeCapture behavior/harness, general Persistence codecs, animation,
whole-shot scene files, UI-wide composition, and PosingCore deletion.

**Contract:** import scheduling returns Pending; the terminal receipt is
published only to the initiating actor/surface generation. Timeout,
supersession, teardown, and stale actor replacement invalidate before rollback;
late callbacks cannot write. Existing native phase ordering is retained.

McdfTransaction owns reading/validating/preparing/applying/redrawing/
committing/rolling back/exporting as an explicit transaction. A redraw-complete
barrier precedes collection/file release; cancellation has bounded shutdown and
drains before port disposal; directory ownership is retained for retry on
timeout/failure. All path existence, reparse containment, temp allocation, and
metadata policy goes through the file boundary. Glamourer/Penumbra/Customize+
remain external owners; Poser owns only its integration assignment and
restore/recovery ledger.

**Tests first:** delayed import/facial callback tests for every receipt state;
actor-generation replacement; rollback and recovery; MCDF redraw barrier,
cancel, shutdown drain, path containment, orphan retry, and external-plugin
unavailable fakes. Test the existing phase sequence before extraction.

**Invariants:** no async success before apply; no post-teardown callback;
no resource deletion before redraw; no direct file IO in Application; no IK
bake behavior or diagnosis is changed.

**Release / Debug:** organizer Release fault/ordinary tests. Debug: required
for exact reviewed import/MCDF/native heads with a 15–30 minute stress card:
import/reset/cancel during redraw and extraction, actor replacement, GPose exit,
reload, verify terminal receipt, collection/texture/temp-directory cleanup,
and report persisted evidence. An unavailable external plugin is not exercised,
not passed; do not run Bake IK.

**Rollback seam:** retain CleanPoseFacade, old integration orchestration, and
old directory ownership until the new transaction's exact-range reviewer and
stress card pass. Extract one transaction only after its boundary tests are
green.

**Review / completion evidence:** one async/integration owner range at a time,
receipt timeline, redraw/cleanup logs, Release results, stress artifact,
finding dispositions, and a recheck of the complete original-base range.

## Slice 9 — animation, presentation, camera, light, and environment

**State owner / sole Luna writer:** one animation/presentation/scene-object
writer, split into sequential animation, presentation, and camera/light/
environment candidates.

**Allowed:** existing Application animation/presentation sessions and Game
ports; Poser.Game/Animation/AnimationRuntimePort.cs and its local
PhysicsFreezePatcher; presentation, camera, lighting, environment, and scene
object paths; Domain value/policy types and focused tests. **Excluded:**
Persistence codecs, MCDF, UI surface ownership, generic scene manager, and Bake
IK changes.

**Contract:** isolate physics patching after expected-byte, instruction-boundary,
layout, fail-closed, and failed-unpatch tests. Retain physics owners until
successful unfreeze; resolve animation targets before removing enforcement;
never write native speed 1 without ownership. Replay/pause and process-global
physics readouts are truthful. Preserve named expression/gaze layers and
restore-only-what-Poser-changed presentation state. Camera/light/environment
retry, origin, visibility, and ownership semantics remain useful; target
relationships that require whole-shot persistence wait for Slice 11.

**Tests first:** pure physics patch layout/fault tests; global ownership and
failed-release tests; speed hand-back/replay; expression redraw/disposal;
presentation external-plugin fakes; camera default retry; world-light
visibility; light/camera round-trip via Slice 7 storage contracts.

**Invariants:** no unsafe access above Game, no process-global state hidden as
selected-actor state, no unconditional hand-back, no timeline authoring, and
external appearance ownership is not duplicated.

**Release / Debug:** organizer Release tests/build/fault fixtures. Debug:
required for animation/physics and native camera/light/environment candidates
after exact review. Cards cover two actors sharing physics ownership, failed
unfreeze/retry, speed owned by another system, expression redraw, GPose camera
retry, original world-light visibility, and cleanup. No Bake IK.

**Rollback seam:** current AnimationRuntimePort, patch path, presentation ports,
camera/light services, and environment holds remain behind tested local
adapters; extraction does not bundle behavior changes.

**Review / completion evidence:** independent per-owner exact-range reviews,
patch bytes/layout evidence, ownership/retry logs, Release results, focused
live artifacts, and explicit list of camera/lighting behavior deferred to
whole-shot work.

## Slice 10 — actual UI surfaces, per-surface state, and read models

**State owner / sole Luna writer:** the UI-surface Luna writer, after the
normative prerequisite is accepted. This writer owns product-surface migration
only; the exact UI-lab/tombstone chain named at the top remains the sole owner
of the lab, UI/testing, and listed PBI cleanup.

**Allowed:** current Poser.UI/** kernel/product assets, typed Application
read-model/action contracts, and one product-surface migration at a time from
the host's current Poser/UI/**; host composition changes needed to wire the
same UI assembly. **Excluded:** Game/runtime/native references, Application
mutation ownership, synthetic lab creation, browser/Picto/golden/capture
replacement, and broad styling framework changes.

**Contract:** record the actual in-game Poser UI baseline before the first
surface cutover. The actual-in-game-only policy is already normative at
integrated head `cdf306e`. Move product surfaces into the current Poser.UI assembly only as
they consume typed Application actions/read models; do not create
Poser.UI.Kernel. UI owns filter/disclosure/hover/picker/dialog, per-surface
selection scope and ephemeral state. It never owns pose accumulation, native
baselines, history, cached native entities, or a singleton pane target. It does
not reference Runtime or native entities.

Surface candidates are attached/detached shell reopening; per-pop-out panes
and disposal; capability/receipt/recovery readouts; and exception-safe style
ownership. Each is a separate range. Visual correctness is accepted only by
the actual in-game UI; standalone component sheets, browser capture, golden
hashes, and synthetic pointer/keyboard labs are not gates and are not replaced.

**Tests first:** Application read-model/action tests and source/assembly checks
that UI cannot reference Game or native namespaces; ordinary pane lifecycle
and disposal tests where available. No synthetic visual test is added.

**Invariants:** one surface owns one ephemeral state set; typed action has one
final outcome; status is visible for pending/applied/rolled-back/recovery;
visual acceptance is in game only; actual product assets remain available.

**Release / Debug:** organizer Release build/test and source checks. Debug:
required for each user-visible surface cutover after exact review, with a
focused card: attached/detached reopen, two pop-outs on different actors,
independent pickers/dialogs/status, reload/close cleanup, capability failure
visibility, and a normal second-plugin visual check. Report observed in-game
behavior and artifact evidence; do not report synthetic hashes.

**Rollback seam:** retain the old host surface behind typed Application
adapters until its in-game card passes; one surface is cut over at a time.

**Review / completion evidence:** actual in-game baseline, per-surface exact
range review/rework, dependency proof, Release output, focused live card, and
the accepted/integrated UI-lab/tombstone-chain head recorded by the organizer.

## Slice 11 — whole-shot and remaining product verticals

**State owner / sole Luna writer:** one Luna writer per listed feature owner;
the organizer sequences them after the safety and UI foundations. No broad
“finish the product” task is valid.

**Allowed:** one feature's Domain/Application/Persistence/Game/UI paths and
its tests/live card at a time. **Excluded:** cross-feature rewrites, moving
Glamourer-owned systems, timeline authoring, and unresolved IK-bake behavior.

**Vertical candidates and disposition:**

| Candidate | Dependencies, owner, and disposition |
|---|---|
| 11A Whole-shot scene save/restore | After 5, 7, 9, and 10. SceneStore plus versioned scene codec and atomic recovery owns actors/props/lights/cameras/environment/relationships; one scene transaction and one read model. Implement as vertical scene files, not a generic scene manager. |
| 11B Nearby overworld actors | After exact spawn handles in 5. Game discovers exact visible identity; Application owns import/receipt; UI exposes the action. |
| 11C Actor/prop/light relationships | Core handle/parent contract starts in 5; persistence/clone/reload completion is a separate 11C range. Missing parent is explicit recovery, never silent detachment. |
| 11D Arbitrary/schema IK | After fixed IK safety, the diagnostic-only qualification tranche, and explicit organizer authorization. New chain definitions require a pure policy and native contract first; until then this gap is Parked, with no Bake diagnosis encoded. |
| 11E Camera target relationships | Base camera behavior is 9; stable target identity, tracking, lock/offset persistence, and missing-target recovery are a scene-model vertical after 11A. |
| 11F Model ID | Poser owns capture/reset/search/metadata and pose-file hint through presentation/storage contracts; equipment/customization/design remain external. |
| 11G Selected/reference import and evaluated-pose mirror/bake | Existing exact native reference and selected-scope paths get separate typed actions. Evaluated-pose mirror is explicit and warned; it must not change safe animation-layer mirror semantics. Bake IK remains excluded. |
| 11H Library grouping/search/metadata and recovery affordances | After Slice 7 bounded index and Slice 10 read models. Groups/tags/author metadata are authored and searched; corrupt/future files are visible and actionable. |
| 11I Posing keybinds/overlay actions and Lips controls | After 9/10 ownership contracts. Keybinds and overlay toggles get real writers; Lips speed/pause follows accepted slot ownership semantics. |

**Tests first:** each candidate starts with ordinary Domain/Application/codec or
fake-port tests, then a focused live card for native timing, scene identity,
ownership, or UI behavior. A feature can be Implemented, Acceptance pending,
Parked, or Rejected only with an owner and evidence; it cannot silently enter
another slice.

**Invariants:** each feature preserves exact-generation identity, explicit
ownership, typed terminal outcomes, useful existing behavior, and the
no-timeline/no-Glamourer-ownership boundaries. No feature may hide a recovery
obligation, create a second scene/selection owner, or widen a neighboring
vertical's writer scope.

**Release / Debug:** organizer Release gates for every candidate. Debug:
required for user-visible/native features; exact card and persisted artifact
are required. A feature that is pure storage can state Debug: N/A with the
reason and still needs Release evidence.

**Rollback seam:** one feature flag/action/codec adapter per candidate; old
behavior remains until the feature's exact range and live card are accepted.

**Review / completion evidence:** one feature owner, one vertical diff,
dependency proof, test/live evidence, product disposition, and independent
full-range review. No candidate may claim parity merely because Brio or Ktisis
has a feature.

## Slice 12 — proof-driven PosingCore/facade/EventBus deletion and final enforcement

**State owner / sole Luna writer:** one final-assembly/deletion Luna writer,
after all prior owners are accepted. Deletion is split into sequential
facade, notification, and project-graph candidates.

**Allowed:** remaining CleanPoseFacade/CleanTransformFacade callers after
their vertical migrations; PosingCore/** files proven unused or migrated;
PosingCore/PosingCore.csproj, PosingCore.Tests references, Poser.slnx,
Poser.Game.csproj, host composition, EventBus/Events and broad legacy
contract callers; source/dependency checks. **Excluded:** new feature work,
the exact UI-lab/tombstone-chain paths owned by that chain, unresolved behavior
fixes, and cosmetic Poser.Game renaming.

**Contract:** delete a facade only after a caller search, local characterization
test, exact-range review, and rollback seam have passed. Delete EventBus only
after every useful notification has a typed Application read-model/action or
direct lifecycle edge; delete verified dead events early only when disjoint.
Remove broad 1:1 legacy interfaces rather than replacing them with a new
generic framework. The final solution has no PosingCore project/reference,
Poser.Game remains the sole unsafe/native/runtime assembly, UI has no
Runtime/native reference, Persistence is host-free if present, and Host is
composition only.

**Tests first:** compile-real project-reference graph checks; rg/source checks
for PosingCore references, unsafe/native imports outside Game, stale generation
writes, raw index ownership, event-order lifecycle, singleton pane state,
detached workers, fire-and-forget cleanup, and hidden async success. Run the
full existing contract/ordinary test inventory after each deletion candidate.

**Release / Debug:** organizer Release build/test and final source/graph gates.
Debug: required only once for the exact final reviewed head after no open P0/P1
findings, with the final full acceptance card and actual in-game UI visual
acceptance. Mechanical deletion candidates are Debug: N/A.

**Rollback seam:** each deletion is a new commit after the last accepted head;
the immediately previous accepted project/adapter remains recoverable in Git.
No reset, clean, stash, amend, rebase, or deletion of an unreviewed worktree.

**Review / completion evidence:** exact-range deletion review, complete graph
proof, Release results/warnings, final live artifact with
AcceptanceQualified=true where required, actual UI acceptance, product-gap
dispositions, and all accepted head SHAs recorded.

The IK-bake result is not release-qualified while the verdict is INSUFFICIENT
EVIDENCE PENDING INSTRUMENTATION; the diagnostic-only tranche and separate
organizer authorization are required before any normative Bake change, and no
unresolved diagnosis is hidden in this result.

## Mapping from the old program and audits

The old Release 0/Train 1–8 program is superseded by the slices above. No
requirement disappears; the map below records its new owner or disposition.

### Old train map

| Old item | New slice or disposition |
|---|---|
| Release 0 clean baseline, redeploy, and full manual smoke | Superseded by accepted T1.1 Slice 0. Do not redo a baseline deployment in this program; organizer uses the accepted SHA and runs the next applicable Release/live card. |
| Train 1.1 contract-test foundation | Accepted transitional Poser.ContractTests coverage at Slice 0; Slice 1 creates/migrates only Poser.Domain.Tests, while Slice 2 exclusively creates/migrates Poser.Application.Tests and owns Application families; later owner slices add their own families. |
| Train 1.2 startup rollback and Gaze | Slice 3. |
| Train 1.3 IK allocation and bone-hook health | Slice 3 for allocation/capability refusal; Bake IK behavior and harness remain on the instrumentation qualification hold. |
| Train 1.4 PBI-012 masks | Slice 1 pure correction and Slice 6 pose transaction callers. |
| Train 1.5 transaction outcome/recovery | Slice 2. |
| Train 2.1 import receipt/late callback | Receipt contract Slice 4; caller/materialization Slice 8. |
| Train 2.2 pending import/IK/facial recovery | Slice 4 epoch contract and Slice 8 import/facial materialization; IK bake excluded. |
| Train 2.3 live harness cancellation/deep cleanup | Slice 3 lifecycle order and Slice 12 final hardening; no Bake IK harness change before the diagnostic-only qualification tranche. |
| Train 3.1–3.4 autosave and GPose exit | Slice 3 lifecycle contract; Slice 7 autosave final capture/queue/join; Slice 9/11 owner migrations. |
| Train 4.1 MCDF | Slice 8 McdfTransaction, redraw barrier, file boundary, and cleanup. |
| Train 4.2–4.3 spawn/companion | Slice 5 core ownership and Slice 11 feature completion. |
| Train 4.4–4.6 environment/world light/default camera | Slice 9, with whole-shot relationships in Slice 11. |
| Train 5 persistence/recovery | Slice 7; visible UI readout in Slice 10. |
| Train 6 animation/native patch | Slice 9. |
| Train 7 UI ownership/style | Slice 10 product surfaces; the complete accepted UI-lab/tombstone chain `727ccb7 -> cb86af7 -> 3ea67f5100ea6808a67f8dcf7d0ab3d22f9f86ea -> 9de84646a5f2d4c84c9609069d92b87100556300 -> c7d2c2e44bd008896d84f05cd716f75bcc7464f4` solely owns `tools/ui-conformance`, UI/testing, UI-workspace, and PBI-011/014/015/015A/016 dispositions/tombstones. The organizer accepted integrated head `cdf306e` after independent no-findings review and Release build/tests (175/175, no Debug/live); actual-in-game-only policy is in force. |
| Train 8 structural extraction/DRY | Local extraction follows each Slice 1–11 contract test; final PosingCore/facade/EventBus deletion and assembly enforcement are Slice 12. The old “structural work last” rule is superseded. |

### Code-health finding map

| Audit finding | New owner |
|---|---|
| 1. Ordered GPose exit and AutoSave capture | Slice 3 phase contract; Slice 7 final autosave implementation; Slice 9/11 participants. |
| 2. Native initialization/Gaze/IK allocation | Slice 3. |
| 3. Zero propagation and malformed masks | Slice 1, then Slice 6 callers. |
| 4. Shared transaction outcome and failed recovery | Slice 2. |
| 5. Async pose import/IK/facial capture | Slice 4 epochs; Slice 8 import/facial materialization; IK bake hold. |
| 6. MCDF redraw lifetime | Slice 8. |
| 7. Spawn index reuse and post-create ownership | Slice 5. |
| 8. Atomic files, validation, AutoSave, library bounds | Slice 7. |
| 9. Animation ownership and physics patch | Slice 9. |
| 10. UI ownership/style/native read leaks | Slice 10; final no-native-outside-Game proof in Slice 12. |

### Backend audit recommendation map and dispositions

| Old R item | New slice or disposition |
|---|---|
| R1 tests in Domain/Application | T1.1 is accepted transitional Poser.ContractTests coverage only. Slice 1 creates/migrates only final Poser.Domain.Tests and retains transitional Application coverage in Poser.ContractTests. Slice 2 exclusively creates/migrates final Poser.Application.Tests and owns Application test families, migrating that coverage only after proof. Missing families continue with their owning slices. R1 is not complete at Slice 0. |
| R2 PosingCore pure-core coverage | Slice 7 format/policy characterization and Slice 12 migration proof; preserve useful tests while the source moves. |
| R3 Gaze degradation | Slice 3. |
| R4 capability-health surface | Slice 3 read model and Slice 10 UI. |
| R5 namespace honesty | Only proof-driven moves in Slice 5/6/9/12; do not cosmetic-rename Poser.Game or introduce a Runtime project. |
| R6 durable migration-state/normative cleanup | This master plan is non-normative and cannot satisfy durable cleanup. Executable Prerequisite 1A assigns the PosingCore/LegacyRuntime role, traversal, naming, namespace, terminology, and deletion-criteria reconciliation to existing product-and-boundaries/posing-runtime homes, with application-state/files-and-transfer and the accepted UI-lab/tombstone chain covering their contracts. Evidence is the accepted diff/link/contradiction and compiler-real ownership record, exact review, and organizer acceptance; no document per class and no duplicate migration plan. |
| R7 ISessionScoped enrollment | Superseded: one explicit SessionLifecycleCoordinator owns phases; no generic enrollment interface. |
| R8 feature-manifest registration | Superseded as a framework: Host keeps explicit composition; per-feature registration may be mechanically split only when it does not add a manifest/manager abstraction. |
| R9 ActorIntegrationSession split | Slice 8 after file-boundary/redraw tests; McdfTransaction only, retain vendor orchestration. |
| R10 physics patcher/live test extraction | Physics patcher Slice 9; live harness changes only for a named safety contract in Slice 3/12, not a blanket split. |
| R11 EventBus hygiene | Verified dead events may be tombstoned in Slice 1; useful EventBus notification callers migrate in Slice 12, then EventBus is deleted. No mediator replacement. |
| R12 native reads from host UI | Slice 10 typed read/action boundary and Slice 12 source proof; all unsafe/native implementation stays in Game. |

### Feature-gap map

| Feature-gap row | New slice or disposition |
|---|---|
| Whole-shot scene save/restore | 11A after 5, 7, 9, 10. |
| Nearby overworld actors | 11B after 5. |
| Actor/prop/light relationships | 5 core; 11C persistence/clone completion. |
| Arbitrary/schema IK | 11D only after fixed-IK safety, the diagnostic-only qualification tranche, and explicit organizer authorization; otherwise Parked. |
| Camera target relationship | 9 base camera; 11E scene relationship/persistence. |
| Model ID ownership/search/reset/metadata | 9 presentation ownership; 11F product completion and storage. |
| Selected/reference import UI | 8 safe materialization contracts; 11G typed UI action. |
| Recovery/bad-file visibility | 7 persisted state; 10 actual UI readout; 11H library affordance. |
| Library grouping/search/metadata | 7 bounded index; 11H authoring/search UI. |
| Posing keybinds/overlay actions | 10 surface ownership; 11I high-frequency product actions. |
| Evaluated-pose mirror/bake | 6 explicit pose action; 11G feature completion; never silently change current animation-safe mirror. |
| Companion clone/detach/status | 5 exact ownership; 11C/11B product completion. |
| Animation Lips controls | 9 runtime ownership; 11I UI completion. |
| Animation-authoring timeline | Rejected product boundary; no slice. |
| Glamourer-owned appearance editing | Rejected product boundary; Glamourer/Penumbra/Customize+ retain ownership. |

The audit's runtime-validation rows are also preserved: slot reuse is Slice 5;
companion variants Slice 5/11; MCDF timing Slice 8; late import/rollback Slice
4/8; foreign speed ownership Slice 9; camera target/light attachment Slice
9/11; and corrupt/bad files Slice 7/10. Each requires the named Release fake or
focused live card; Not exercised never counts as pass.

## Program completion

The program is Accepted only when the organizer has recorded all of the
following against exact reviewed heads:

- PosingCore has no project, source caller, or solution/reference edge;
  Poser.Game remains the sole unsafe/native/runtime assembly; the dependency
  graph is compiler-real and matches the minimal target.
- Domain math/policies are pure; Application owns logical session/scene,
  transactions, outcomes, history, recovery/read models, and typed actions;
  host-free Persistence owns codecs/stores/autosave/library/quarantine when
  present; UI owns per-surface ephemeral state; Host only composes.
- There is one SceneStore, one exact BindingRegistry boundary, one
  SessionLifecycleCoordinator, one mutation owner, and no stale-generation
  writes, index ownership, late callbacks, hidden async success, silent
  rollback failure, event-order lifecycle, singleton pane state, detached
  workers, or fire-and-forget file/resource cleanup.
- Portable poses and scene files are versioned, validated, atomic, structurally
  identified, ambiguity-visible, and compatible with useful existing formats;
  recovery is persisted and visible.
- The accepted UI-lab/tombstone chain is integrated at `cdf306e`, the actual
  in-game UI is the sole visual oracle, and the synthetic UI lab is deleted and
  not replaced. Product assets and retained feature/format behavior remain.
- Every feature-gap row is Implemented, Acceptance pending, Parked, or
  Rejected with a current owner and rationale. Timeline authoring and external
  appearance ownership remain rejected boundaries.
- Every slice has local tests/characterization, exact-range review/rework,
  organizer Release evidence, required live artifact or explicit Debug: N/A,
  finding disposition, rollback evidence, and an accepted-head SHA.
- The final exact reviewed head passes the full Release gates and the required
  final in-game card. The final live artifact is AcceptanceQualified=true
  where the program acceptance contract calls for it. The IK-bake hold is
  separately adjudicated; no unresolved diagnosis is hidden in this result.

New discoveries become a linked PBI or an explicit Parked decision. They never
silently expand a slice.

## Organizer handoff template

~~~text
TL;DR: <state>; <next action or blocker>.
Slice / Luna role: <id> / <effort>
Status: <Review | Rework | Automated pass | Acceptance pending | Accepted>
Base..candidate: <exact range>
Changed: <commits and paths>
Owner/state: <one mutable owner>
Characterization/tests first: <commands/results>
Release gates: <exact commands/results/warnings>
Deployment: <N/A with reason, or Debug auto-deployed exact SHA after notice>
Live card: <prerequisite, exact actions, expected, cleanup, evidence>
Rollback seam: <adapter/feature flag/accepted head>
Review: <independent ranges and finding dispositions>
Evidence: <artifact paths, readouts, accepted head>
Next owner/action: <one concrete step>
~~~
