# Backend Maintainability Audit

Branch: `feature/imperative-rebuild` (452 commits ahead of main). Date: 2026-08-03.
Scope: the posing/scene backend — PosingCore, Poser.Domain, Poser.Application, Poser.Game (incl. LegacyRuntime), host composition. UI rendering excluded.
Method: six parallel evidence-gathering passes (module boundaries, discoverability, contracts/lifetimes/threading, testability, sig/offset risk, structural comparison vs Brio and Ktisis 0.4). Every claim below carries a file:line citation; scores are honest, not graded on a curve.

> **Snapshot warning (2026-08-12):** This is a dated, non-normative and
> untrusted historical audit snapshot. It is retained as evidence, not as a
> current contract or status ledger. The test, scenario, and harness statements
> below were corrected where materially false against current `HEAD` plus the
> inherited working tree; re-verify all remaining findings against source.

Framing question: *how would we write this if we weren't the sole developer — what does a future contributor need to work on this safely?*

## Scores at a glance

| Dimension | Score | One-line verdict |
|---|---|---|
| Module boundaries | 6/10 | Acyclic project graph, uniform port pattern — but the two biggest classes are grab-bags and the legacy quarantine is folder-deep only |
| Discoverability | 4/10 | Behavior docs are accurate; *where code lives and which path is live* is tribal knowledge; the migration itself is undocumented |
| Contracts / lifetimes / threading | 6/10 | New ports enforce threading in the type; legacy layer runs on vigilance and a shared-thread coincidence |
| Testability | **4/10** | `Poser.slnx` contains `PosingCore.Tests`; the live harness has eight scenarios, but clean-layer and native lifecycle coverage remains sparse |
| Sig/offset risk | 6.5/10 | Small, mostly well-degraded surface — with one deliberate whole-plugin landmine (GazeService) |
| vs Brio / Ktisis | — | Ktisis 0.4 is structurally the closer cousin (confirmed); Poser wins on failure model, identity, history, portability; loses on session lifetime and feature legibility |

The rebuild's *direction* is right. The gap is enforcement: the rules that make the new layers good are conventions, not structures, exactly where a second contributor would break them.

---

## 1. Module boundaries — 6/10

### What holds

- **The project graph is genuinely acyclic and compile-enforced.** `Poser.Domain` references nothing; `Poser.Application` references Domain only (verified at the `using` level too — every import is System/Domain/Application); `Poser.Game` references Domain + Application + PosingCore; the host references all. No Domain→Game, no Application→LegacyRuntime, no cycles. Brio and Ktisis are single-assembly — their layering is directory convention; ours physically cannot see Dalamud from Domain.
- **The port pattern is uniform, not ad hoc.** Every Application port interface has exactly one Game implementation wired in `Poser/Composition/ServiceRegistration.cs` (`ITransformRuntimePort` :86, `IIkConfigurationPort` :95, `IAnimationRuntimePort` :99, `IPresentationRuntimePort` :104, `IMcdfFileBoundary` :108, `IIntegrationRuntimePort` :109). Ports uniformly translate stable ids via `StableBindingRegistry` and wrap legacy services.
- No partial-class sprawl anywhere (zero `partial class` in backend projects).
- Exemplary single-job classes exist and prove the target shape works: `CleanSceneLifecycle` (277 lines, documented contract), `StableBindingRegistry` (287), `EditorState` (23), the four-file `Poser.Application/Transforms` split.

### What doesn't

- **`Poser.Application/Integration/ActorIntegrationSession.cs` — 1723 lines, five jobs**: Glamourer design management (:55–:178), CustomizePlus profiles (:201–:267), a ~600-line MCDF import/export async state machine (:707–:1373), scene reconciliation (:616), orphaned-directory retry bookkeeping (:1315). Worse: despite `IMcdfFileBoundary` existing precisely to keep IO out of Application, it does direct `System.IO.File.Exists` (:1561) and `Directory`/`FileInfo` work (:1673–:1675). The layer that is supposed to be the disciplined one contains the codebase's worst class, and it leaks around its own port.
- **`Poser.Game/Animation/AnimationRuntimePort.cs` — 1125 lines**: nominally one port, actually hooks + 5 sigs + timeline read/blend + emotes + slot speeds + lips + stance + weapon draw + position lock + scrub + a **process-global physics-freeze code patcher** writing NOP bytes into game code (:1039–:1081). The patcher is a different concern with a different blast radius and belongs in its own class.
- **`Poser.Game/Validation/LiveTestService.cs` — 1616 lines** of test harness compiled into the production Game assembly, importing both legacy and clean worlds. Deliberate (live-smoke gate), but it is the largest class in Poser.Game and it is not product code.
- **`LegacyRuntime/BonePosingService.cs` — 1107 unsafe lines** holding transform application, the IK configuration store, pose-stack capture, region resets, mirroring, animated-baseline cache, and linked-bone toggling. "Parked" is cosmetic: it is the live registered write engine (ServiceRegistration.cs:79).
- **Native knowledge leaks above Poser.Game** (the de-facto interop home):
  - `PosingCore/Entities/Skeleton.cs` — the *entity* imports FFXIVClientStructs, exposes `GameSkeleton*` (:117, :132), walks `AccessBoneModelSpace` (:269), and hardcodes raw offsets `0x2A0`/`0x2A4` (:23).
  - `PosingCore/Entities/ActorBase.cs` — unsafe `GameObject*` reads (:108, :123).
  - `PosingCore/Game/ExpressionService.cs` — unsafe `Character*` reads (:139); also the only legacy runtime service *not* moved to LegacyRuntime.
  - `Poser/UI/Panes/GraphicalBonePane.cs` — **host UI** importing FFXIVClientStructs (:9) with `unsafe GetHeadSectionForActor` dereferencing the actor address (:267). The clearest wrong-layer leak in the repo.
- **Clean classes bypass their own ports**: `CleanSceneLifecycle` injects concrete `AnimationRuntimePort` (:32, :57) for `SyncEnforcementIndex`; `TransformRuntimePort` takes concrete `PosingService` + `BonePosingService` (:30) for internals beyond the interfaces; ServiceRegistration registers concrete+interface for the same singleton (:99–:101), institutionalizing it.
- **`CleanPoseFacade` is a 13-dependency bridge** (:19–:32) mixing five legacy interfaces with six clean services. Honest as a transitional adapter — but see §7: it should die with the migration, not be "fixed".

## 2. Discoverability — 4/10

### Feature-to-file reality check (10 traces)

Findable by name alone: **spawn actor, gaze, undo, MCDF** (MCDF is the model trace: Domain format → Application session → Game boundary/port, each named what it is). Everything else needs tribal knowledge:

- *Rotate a bone*: 4 hops across 3 projects (`CleanTransformFacade` → `TransformGestureService` → `TransformRuntimePort` → `LegacyRuntime/BonePosingService`), nothing named "rotate", and the terminal engine is labeled "Legacy".
- *Freeze physics*: lives under **Animation** (`AnimationSession.cs:451` + the NOP patcher in `AnimationRuntimePort`); the only file named "Physics" is a bone-category decoy.
- *Actor appearance*: split across two subsystems with neither named "appearance" — native part is "Presentation" (`PresentationRuntimePort`), external part is "Integration" (Glamourer/Penumbra/C+).
- *Camera orbit*: a trap. `LegacyRuntime/CameraService.cs` promises camera control; it is read-only projection math for the gizmo. The camera workspace is now retained; the old deferred note is superseded by `product-and-boundaries.md`.
- *Six classes named "pose-something"* (`PosingService`, `BonePosingService`, `PoseEditService`, `PoseFileService`, `PoseTransferService`, `CleanPoseFacade`) with no doc disambiguating them — and `PosingService` is *not* the posing service (it applies actor model-transform overrides).

### Namespace landmines (the worst category)

- `PosingCore.csproj:9` sets `RootNamespace=Poser` — `using Poser.Services;` points you at a project named PosingCore.
- `namespace Poser.Game` spans **two projects** (PosingCore/Game and Poser.Game), and `LegacyRuntime/*.cs` declares plain `Poser.Game` — the *only* legacy marker is the folder path, invisible in code, imports, and stack traces. At a call site, legacy `SkeletonService` and clean `CleanSceneLifecycle` are indistinguishable.
- `namespace Poser.UI` also spans two projects (Crystarium library vs product UI).
- Six empty decoy directories sit exactly where a contributor would search: `PosingCore/{History,IPC,Library,Validation}`, `Poser.Game/{History,Selection}`.

### Docs

The four architecture docs are dense, current, and name real classes — rare and worth saying. But:

1. **The backend migration is undocumented.** Grep for "LegacyRuntime" in `docs/` → zero hits. Nothing records which features run on legacy vs clean paths (answer, recoverable only from code: *all native writes terminate in LegacyRuntime; the clean layer is orchestration on top*), what PosingCore is today, or what qualifies a service to leave LegacyRuntime. `ServiceRegistration.cs` is the de-facto migration document.
2. **The old testing claim was false.** The current canonical testing doc records
   manual in-game visual acceptance for the real Poser UI and keeps it distinct
   from the live native gate; Release is the non-deployment validation
   configuration and Debug is deployment-only.
3. The historical PBI-016 status line and duplicate PBI-015 IDs required
   reconciliation; the current backlog now records acceptance state and uses
   PBI-015A for the legacy inventory. `docs/brio/` contains the retained parity
   audit; `docs/ipc/` is not a normative document home.
4. Terminology collision: "legacy" in the backlog docs means the deleted reactive **UI**; "Legacy" in the source tree means the parked **backend runtime**. Same word, two migrations, zero cross-reference.
5. Omission-as-lie: `posing-runtime.md:24` presents `TransformRuntimePort` as "the one native write path" without saying it bottoms out in `LegacyRuntime/BonePosingService`, describing the target architecture as if it were the current one.

## 3. Contracts, lifetimes, threading — 6/10

### Interfaces

- **The twelve old `I*Service` interfaces are 1:1 mirrors**: one implementation each, zero test fakes anywhere. `IGPoseService` and `IIKService` have **one member each**; `IBonePosingService` has **21**, mixing transforms, IK config (now duplicated by `IIkConfigurationPort`), mirroring, snapshots, and region resets — plus orphaned doc comments for deleted members.
- **Ports have better rationale** (stable ids, no pointer leakage, threading contract in the doc — `IAnimationRuntimePort.cs:14–27`) but fatter surfaces: `IAnimationRuntimePort` ≈28 members, `IIntegrationRuntimePort` ≈30 (three vendors behind one interface). `ITransformRuntimePort` (3) and `IIkConfigurationPort` (5) show what right-sized looks like.
- The implicit policy — *interfaces only at the native boundary; Application sessions are concrete* — is consistent and defensible. It is stated nowhere.

### EventBus (`PosingCore/Core/EventBus.cs`)

Handler-list copy under lock, synchronous delivery on the publisher's thread, per-handler catch. Findings:

- **`BoneTransformChangedEvent` is dead**: 6 publish sites (`BonePosingService.cs:676,832,889,1010,1037`), zero subscribers, plus a misleading interface comment claiming it's how changes are surfaced.
- **`SkeletonChangedEvent` is a direct call in a bus costume**: one publisher (`SkeletonService`), one subscriber (`CleanSceneLifecycle:258`) that discards the payload and calls `Refresh()` — and the pair is synchronous-recursive, requiring the `_refreshing` re-entrancy latch (:144–:160) to survive itself.
- Most `ActorListChangedEvent` subscribers ignore the payload and re-read the world.
- **`ExpressionService` subscribes and never unsubscribes** (:100) — it has no `Dispose` at all. Masked only by `EventBus.Dispose` clearing all handlers.
- `Events.cs:33` contains an orphaned "Selection Events" region: doc comments whose types were deleted.

### Lifetimes

All ~79 registrations are singletons; container-owned disposal in reverse-creation order; every `Framework.Update +=` has a paired `-=` in Dispose (verified per file). `CleanSceneLifecycle.Dispose` (:81–:142) is the most careful teardown in the codebase (framework-thread inline, else bounded 2s wait with abandon gate). One structural hazard: a ctor throw during container resolution in `Poser.cs:95` means `Poser.Dispose` never runs, orphaning already-enabled native hooks — which is exactly what the GazeService landmine (§5) would trigger.

### Threading — two regimes

- **New ports: enforced in the type.** Every mutating entry checks `IsInFrameworkUpdateThread` and returns a typed failure (`TransformRuntimePort.cs:209`, `AnimationRuntimePort.cs:193+`, `PresentationRuntimePort.cs:111`, `IntegrationRuntimePort.cs:230`, `ViewportProjection` throughout). Check-and-refuse rather than a dispatcher — the honest version. `LiveTestService` marshals every native touch via `RunOnFrameworkThread` (14 sites).
- **LegacyRuntime: zero thread checks** (the only lock in the directory is GazeService's `_sync`). `BonePosingService.ApplyTransform` and `PosingService.SetTransformOverride` mutate unlocked dictionaries that native detours read every frame; `ActorSpawnService` calls the native object manager on the caller's thread from UI panes. De-facto safe **only** because Dalamud's ImGui draw and Framework.Update share the main thread — an assumption stated and checked nowhere. Any future background caller (Task, IPC) silently races.

## 4. Testability — 4/10

The original zero-coverage premise is superseded by the current tree:

- `PosingCore.Tests/PosingCore.Tests.csproj` is in `Poser.slnx` and currently
  has 14 C# sources covering pose math, import/clipboard, expression, rest-pose,
  reference-pose, and AutoSave behavior.
- `/poser test` (with the retained `/poser selftest` alias) routes to
  `LiveTestService`; `LiveScenarioCatalog` defines eight scenarios and the
  harness requires a live GPose session. Real UI visual acceptance is manual and
  in-game; ordinary contract tests are not substituted by synthetic pixels.
- No Domain/Application test project is present in the current solution. Clean
  application history/gesture seams, binding resolution, MCDF, and most native
  lifecycle failure paths remain sparse or dark; this audit does not treat the
  live harness as a substitute for those tests.

The architecture has earned testability — `Poser.Domain` has zero references,
`Poser.Application` references Domain only, and the ports provide constructor
seams — but only part of that surface is covered. Untestable-by-construction is
still concentrated where it is inherent: hooks, Havok walks, pointer-wrapper
entities, and concrete native runtime ports.

## 5. Sig/offset risk surface — 6.5/10

### Inventory

**12 raw signatures**: 5 in `AnimationRuntimePort` (SetEmoteMode :121, CancelTimeline :123, PlayEmote :125 — deliberately the 4-arg shape vs CS's 2-arg binding, overall-speed hook :133, physics-freeze patch site :159); 3 in `IKService` (:46–:50, CCD + TwoJoint); 2 in `GazeService` (:90, :93); 2 in `BonePosingService` (UpdateBonePhysics :114 — **the hook all bone posing runs inside** — and FinalizeSkeletons :127).

**3 CS-resolved hook points**: SetSlotSpeed (`AnimationRuntimePort.cs:148`), SetPosition (`PosingService.cs:53`, with a framework-tick fallback so the feature degrades rather than dies), and `PresentationRuntimePort.cs:84` — a hand-computed CharacterBase **vfunc at vtable+0xC0** with the index argued from named neighbors.

**5 hand-written offset clusters**: `Skeleton.cs:23` scale factors `0x2A0/0x2A4` (silently wrong on shift — gizmo drift, no crash, no detection; should be checked against current CS symbols); `GazeService.cs:558–:598` full hand-written look-at structs incl. a `0x1E0` stride slot computation; `IKService.cs:214–:271` Havok solver layouts (low volatility); `AnimationRuntimePort.cs:38` patch offset + assumed instruction widths (sig can match while widths changed → NOPs land mid-instruction; rollback is transactional but can't detect wrong-width); the 4-arg PlayEmote calling convention (drift = crash at call time). Object-table conventions (GPose slots 201+, index 0 = player) are inline constants in `ActorManager.cs:22` / `ActorSpawnService.cs:58`.

Everything else goes through published FFXIVClientStructs layouts — patch risk delegated to the CS bump, the ecosystem-standard position. There is **no central patch-day file**: a patch checklist must enumerate 6 files. The new layers frame this as design ("Addresses exist ONLY inside this class", `AnimationRuntimePort.cs:25`) — defensible for encapsulation, bad for auditability.

### Degradation policy

The implicit standard — scan under try/catch, latch capability flags on successful enable, refuse per-operation with an explicit user-visible failure — is met by AnimationRuntimePort (all 6 natives, each with an explicit `Fail` path), PresentationRuntimePort, PosingService, IKService, BonePosingService. `AnimationRuntimePort.cs:584–:609` even documents *refusing* to inherit Ktisis's `0x2D0` force-loop offset, with a written proof of why it's untrustworthy. That is the best version of this discipline in any of the three codebases.

**One violation, and it's deliberate — `GazeService.cs:89–:95`**: `// No try-catch - let plugin fail to load if sigs are invalid`, followed by two unguarded `ScanText` calls and an unguarded hook enable. Blast radius: registered at `ServiceRegistration.cs:142` → injected into `PoseInspectorPane` → eagerly resolved via `GetRequiredService<IUIManager>()` at `Poser.cs:95` with no catch. **One stale gaze sig after a patch = the entire plugin fails to load**, and because the throw happens mid-resolution, `Poser.Dispose` never runs — GPoseService's framework hook and BonePosingService's two *enabled native hooks* are orphaned. This is verbatim the Brio bug class the project defines itself against (anchored: `Brio/Game/GPose/GPoseService.cs:63–:93` and seven other Brio services ctor-throw the same way, with `Brio.cs:130` rethrowing so any one dead sig bricks Brio).

**The inverse inconsistency**: legacy failures are *silent*. A failed `UpdateBonePhysicsHook` (BonePosingService:119) or IK init (`_initialized=false`, Solve no-ops at :78) reduces the plugin's *central feature* to inert with only a log line — nothing surfaces to the UI. "Loads fine, bones don't move, no message" is arguably worse UX than Gaze's hard failure. The two files embody opposite philosophies; neither matches the ports' explicit-refusal contract.

Residual risk nobody handles (us, Brio, or Ktisis): a sig that false-positive matches different code installs a hook on the wrong function and crashes at fire time. No sanity probes exist.

## 6. Structural comparison — Brio and Ktisis 0.4

**Confirmed: Ktisis 0.4 is the closer cousin.** Interface-per-manager ↔ our ports; compile-time capability interfaces (`Scene/Decor/ITransform.cs`) ↔ our typed target-kind switch; memento→history ↔ our before/after `TransformPatch`; per-hook log-and-bool degradation ↔ our per-operation refusal; explicit construction ↔ our explicit `ServiceRegistration`. Brio's shape — entity tree with runtime `Dictionary<Type, Capability>` bags, service-locator ctors, a mediator that is really a lifecycle-tick pump (~10 message types; the hot posing path bypasses it) — is the outlier, not us.

**What they have that we lack:**

1. **Per-GPose lifetime as a structure (Ktisis).** `ContextManager.cs:41` destroys and rebuilds the whole editor graph per GPose session; hooks live in a `HookScope` disposed with it; a stale context throws loudly (`EditorContext.cs:44`). Our equivalent is `CleanSceneLifecycle.OnGPoseChanged` calling `Clear()`/`ResetAll()` on a hand-listed set of session singletons — every new stateful service must *remember to enroll* in a 10-dependency constructor. Ktisis's guarantee is structural; ours is a convention that will rot.
2. **Feature legibility (Brio).** "What can an actor do?" is one screen (`ActorEntity.OnAttached`, :106–:144); adding a feature is capability file + widget file + one line, near-zero merge conflicts. We have no single artifact that enumerates features — the answer is spread across `ServiceRegistration` sub-blocks wired ad hoc (compare the `ActorIntegrationSession` factory lambda :112–:127 against the plain registrations around it).
3. **Declarative hook modules (Ktisis).** `[Signature]`-attributed fields grouped per module with scope-wide enable and per-field failure capture (`HookModule.cs:93`) — auditable native touchpoints per module, vs our copy-pasted scan-and-degrade ctor blocks.
4. **Hierarchy as data (both).** Weapon/prop attachment has a home in both (entity tree; `IAttachable`). Our `SceneSnapshot` is flat; attachment has no home yet.

**What we do better — with evidence, not vibes:**

1. **Failure model.** Per-operation degradation with explicit user-facing failures vs Brio's eager-init rethrow (one dead sig bricks the plugin) and Ktisis's silent bool. Modulo GazeService (§5).
2. **Identity.** `StableBindingRegistry` generation-tracked ids with typed `BindingStatus` (:8–:23) — neither reference has anything like it; Brio holds live object references in capabilities, Ktisis holds raw pointers guarded by `IsValid`. Redraw survival and "a redrawn actor can never inherit the previous body's speed enforcement" (`CleanSceneLifecycle.cs:187`) are direct payoffs.
3. **History.** Bounded before/after patches with `Reconcile` dropping only stale-target patches (`TransformHistory.cs:69`) vs Brio cloning entire `PoseInfo` into untyped `object` and Ktisis mementos dangling across context death.
4. **Pose portability.** `PortablePose` deliberately excludes native indices and generations (`PortablePose.cs:9`); layered `PoseDelta`s with `InteractiveOnly()` capture filtering vs Brio exporting raw transforms via five copy-pasted slot loops.
5. **Compile-enforced layering** (§1) — unique among the three.
6. **Threading/coalescing discipline.** Structural-signature refresh coalescing with documented backoff vs Brio's `delayTicks: 2 // TODO: Why do we need to wait several frames for some users?`.

## 7. What should NOT change

Things that look odd but are load-bearing. A future contributor (or a future us) will be tempted; don't.

- **The delta-pure export model.** Poses serialize as layered deltas with actor-independent bone identity. It is why files survive redraws, race swaps, and patches. Both references do it worse.
- **Per-operation sig degradation and check-and-refuse threading.** More verbose than a dispatcher or an init-throw; it is the honest failure model and the reason patch days are survivable. Extend it (§8); never trade it away.
- **Eternal singletons + reconcile-in-place**, not Ktisis-style per-session graph destruction. Our reconcile model survives mid-session actor churn (redraw, MCDF swap) that context destruction would lose state over. Fix the *enrollment* problem (R7), not the lifetime model.
- **No mediator, no runtime capability bags.** Brio's mediator is a lifecycle pump our EventBus already covers; its capability dictionary is runtime-typed discovery our compile-time target-kind switch does with compiler checking. The indirection buys a 9-contributor team merge isolation; it costs everyone else call-site legibility. We are not a 9-contributor team.
- **Interfaces only at the native boundary; concrete Application sessions.** Twelve 1:1 mirror interfaces (§3) are the *counter-example* — don't repeat them by interfacing the sessions. Fakes belong at the ports.
- **`CleanPoseFacade` / `CleanTransformFacade` as 13-dep bridges.** Transitional by design; they die when the panes migrate. Splitting or beautifying them now is wasted motion.
- **`LiveTestService` in the Game assembly.** The live-smoke gate needs production wiring to be a wiring gate. Trim it (R10), don't extract it into a project that would need `InternalsVisibleTo` everywhere.
- **The refusal to inherit unproven offsets** (`AnimationRuntimePort.cs:584–:609`). Written proofs for skipped features are a feature.

## 8. Remediation plan

Ordered. Each item is one focused session, sized for the working system — no rewrites. Risk classes: **pure-move** (compiler-chased, no behavior), **docs-only**, **behavior-touching** (scoped, testable).

| # | Item | Risk | Payoff |
|---|---|---|---|
| R1 | **Put tests back in git.** Create `Poser.Domain.Tests` + `Poser.Application.Tests` (xunit, in `Poser.slnx`, committed first thing). Seed with the highest-value pure targets: `TransformHistory` incl. `Reconcile` staleness, `SelectionSession`, `PoseLayers`/`PortablePose` invariants, `TransformGestureService` begin/update/commit/cancel against a fake `ITransformRuntimePort`. | pure-add | Ends the zero-coverage state; first-ever tests for the new layers; a fake port proves the seams are real |
| R2 | **Re-cover PosingCore's pure core.** New `PosingCore.Tests` project: PoseMath, BonePoseInfo delta/stack/propagation, LinkedBones, all file converters (Anamnesis/CMTool/legacy names/expression import), EventBus. The old 216-test list (recoverable from the stale Jul 23 binary via `-list tests`) is the checklist. | pure-add | Restores the regression net over the file formats and math the whole plugin stands on |
| R3 | **Defuse GazeService.** Wrap the two `ScanText`s + hook enable in the same try/catch + capability-flag pattern as `AnimationRuntimePort`; gaze operations refuse explicitly when dead. | behavior-touching (failure path only) | Removes the one whole-plugin-death landmine; policy becomes consistent: zero ctor-throw sites |
| R4 | **Surface silent capability loss.** Expose per-capability health (bone-posing hooks, IK chains, gaze, stance/emote/speed natives — the flags already exist internally) via one status surface: a `/poser status`-style readout **and** a visible indicator in the UI (no command-gated-only features). | behavior-touching (additive) | "Loads fine, bones don't move, no message" becomes diagnosable in seconds on patch day |
| R5 | **Namespace honesty.** Move `LegacyRuntime/*` to `namespace Poser.Game.LegacyRuntime`; move `ExpressionService` from PosingCore into LegacyRuntime; delete the six empty decoy directories. (Leave `RootNamespace=Poser` — churn outweighs payoff mid-migration; document it in R6 instead.) | pure-move | Legacy becomes visible at call sites, in imports, and in stack traces; the quarantine becomes compiler-real |
| R6 | **Write `docs/architecture/backend-migration-state.md`** — the missing story: what PosingCore is today, what LegacyRuntime is, which features run on which path (all native writes → legacy engines; clean layer orchestrates), the naming rules (three meanings of "Service", the "Clean" prefix, interface policy), LegacyRuntime exit criteria per service, and the namespace quirks. Fix `testing.md`'s false "no harness exists" claim and the PBI-016 status line; delete the empty `docs/brio` + `docs/ipc` dirs or mark them superseded. | docs-only | The single highest-leverage onboarding artifact; kills the two normative-doc contradictions |
| R7 | **`ISessionScoped` enrollment** (the minimal Ktisis steal): one interface (`OnSessionEnd`/`Reconcile`); `CleanSceneLifecycle` takes `IEnumerable<ISessionScoped>` instead of six hand-listed constructor deps; session services register as both themselves and `ISessionScoped`. | behavior-neutral refactor | Forgetting to enroll a new stateful service becomes impossible instead of a latent GPose-exit state-leak |
| R8 | **Feature-manifest registration** (the minimal Brio steal): split `AddPoserCore`/`AddPoserFeatures` into per-feature methods (`AddTransformFeature`, `AddAnimationFeature`, `AddIntegrationFeature`, …), each holding exactly that feature's port+session+facade+pane lines. Same registrations, zero behavior change. | pure-move | "What features exist and which files make one" becomes one screen; the de-facto migration doc becomes a real manifest |
| R9 | **Split `ActorIntegrationSession`** (two sessions if needed): first route all direct `System.IO` through `IMcdfFileBoundary` (closing the port leak); then extract the MCDF import/export state machine into its own class, leaving the session as vendor orchestration + reconcile. | behavior-touching | The worst class in the disciplined layer becomes three testable pieces; R1's fakes extend to MCDF |
| R10 | **Extract the physics NOP patcher** from `AnimationRuntimePort` into its own class with its own capability flag; factor the copy-pasted scan-and-degrade ctor blocks into a `TryHook(sig, detour, featureName)` helper (the minimal Ktisis hook-module steal — helper, not attribute framework). Trim `LiveTestService`'s inline scenario bodies into per-scenario files under `Validation/`. | pure-move | The process-global patcher stops hiding inside a per-actor port; new natives get degradation by construction, not by copy-paste |
| R11 | **EventBus hygiene.** Delete the dead `BoneTransformChangedEvent` (6 publishes, 0 subscribers) and its false interface comment; delete the orphaned "Selection Events" comment region and `IBonePosingService`'s dead doc stubs; give `ExpressionService` a `Dispose` with unsubscribe. Consider replacing the `SkeletonChanged` publish→`Refresh()` pair with a direct call and deleting the re-entrancy latch — measure first. | behavior-neutral (last part behavior-touching) | Removes false signals that actively misdocument the event model |
| R12 | **Pull native reads out of the host UI.** `GraphicalBonePane`'s unsafe head-section/`Character*` access moves behind `PresentationRuntimePort` (or a small dedicated read port); the pane loses its FFXIVClientStructs import. Longer-term same treatment for `Skeleton.cs`'s `0x2A0/0x2A4` (first check whether current CS already names those fields — if so this becomes a deletion). | behavior-touching (small) | Closes the last wrong-layer native leaks; §5's patch-day checklist shrinks |

Sequencing logic: R1–R2 first because every later behavior-touching item (R3, R4, R9, R11, R12) should land with tests, and because zero-coverage is the most dangerous single fact in this audit. R5–R6 next because they are cheap and every subsequent session benefits from honest names and a written map. The comparative steals (R7, R8, R10) are deliberately the *minimal* versions — no capability system, no per-session graph, no attribute framework.

### Patch-day appendix

Until R4/R10 land, the sig/offset checklist on a game patch is exactly six files: `AnimationRuntimePort.cs` (5 sigs + patch offsets + 4-arg PlayEmote), `GazeService.cs` (2 sigs + hand-written structs), `BonePosingService.cs` (2 sigs), `IKService.cs` (3 sigs + Havok layouts), `PresentationRuntimePort.cs` (vtable+0xC0), `Skeleton.cs` (0x2A0/0x2A4). Plus the CS version bump for everything else.
