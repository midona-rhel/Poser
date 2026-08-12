# Poser master remediation and feature plan

Date: 2026-08-12. This is the single execution plan for the code-health and
feature audits. It is a dated non-normative plan; durable contracts remain in
the homes indexed by `docs/README.md`.

## Permanent operating rules

- The organizer never authors or fixes production code. The organizer owns
  specifications, scope, review, finding disposition, authoritative Release
  builds/tests, deployment, and acceptance. Luna worktree chats author every
  repository implementation and every accepted fix.
- Implementation chats are not required to run broad builds or test suites.
  They may run a narrowly delegated diagnostic, but the organizer runs the
  authoritative Release gates. Build or test failures return to the same Luna
  implementation chat; the organizer does not repair the candidate.
- **Debug builds auto-deploy to the live game.** Never run Debug for ordinary
  compilation, tests, fault injection, or review. Use Release for those gates.
  Run Debug only as the announced deployment action for an exact reviewed head
  when the user is ready to test in game.
- All specification, implementation, fix, and review tasks use Luna. Use Luna
  Max for native, lifecycle, asynchronous, exit, spawning, MCDF, patching, and
  cross-window ownership work. Luna high/medium may handle pure tests, docs,
  mechanical moves, and small file/UI changes. Every Max-risk implementation
  receives an independent Luna Max review.
- One implementation task owns one branch, worktree, and state owner. Reviewers
  are independent and read-only. The implementation task never reviews itself.
  The organizer alone accepts, rejects, or defers findings.
- Every ongoing and final report begins `TL;DR: <state>; <next action/blocker>`.
  Required testing is given as a concrete card: prerequisite, exact actions,
  expected result, cleanup, and evidence to report.
- Every Luna task must explicitly send its complete ongoing blocker or final
  report to the organizer task with the task-messaging tool before ending.
  A final answer left only in the child task is insufficient.
- Feature work never silently enters a code-health tranche. New discoveries
  become linked PBIs or explicit parked decisions.

## Tranche state and evidence

States are: `Planned -> Spec ready -> Implementing -> Review -> Rework ->
Automated pass -> Acceptance pending -> Accepted`. Exceptional states are
`Blocked`, `Reverted`, `Superseded`, and `Parked`, each with an owner and reason.
“Complete” means Accepted, not compiled, deployed, merged, or apparently working.

At tranche start the organizer records:

- immutable base SHA/tag, unique `codex/` branch/worktree, owner and Luna effort;
- one behavior contract, owned paths/state, exclusions, and preserved invariants;
- automated and live gates, or `live gate: N/A` with reason.

The Luna implementation chat is the sole production committer. Reviewers inspect the
exact `BASE..CANDIDATE` range and report file/line evidence without editing,
committing, rebasing, or cherry-picking. The organizer records each finding as
accepted, rejected with rationale, or deferred to a PBI. Accepted fixes are new
commits by the same Luna implementation chat on the same branch. Build/test
failures are also returned to that chat; the organizer never repairs the range.
The affected reviewer rechecks the fix commits, then another reviewer checks the
complete original-base-to-new-head range.

Only the exact reviewed head may deploy. Its SHA, changed paths, commits, Release
commands/results, warnings, artifact identity, live verdict and accepted head are
recorded. The next tranche starts from that accepted head. Combining parallel
production ranges requires a fresh combined-range review.

## Build, deployment, and live gates

Safe non-deployment gates:

```powershell
dotnet build Poser.slnx -c Release --no-restore --nologo
dotnet test Poser.slnx -c Release --no-restore --nologo
```

If restore is legitimately required, record and review that separately. Never
substitute Debug because Release assets are missing.

Deployment gate:

1. exact head has no open P0/P1 finding and passes required Release gates;
2. user is told `Debug build will auto-deploy <SHA>` and confirms readiness;
3. run the repository's Debug build once from that reviewed worktree;
4. do not edit or build another head until the live card is complete.

Live tests use the smallest applicable command:

```text
/poser test <scenario-id> --iterations 1
/poser test status
```

Current scenario IDs are `selection.actor-bone-clear`,
`transform.actor-components`, `transform.actor-undo-redo`,
`posing.bone-components`, `posing.animation-interference`,
`posing.reset-region`, `posing.copy-paste-pose`, and `posing.ik-bake`.
`/poser test full` means all eight scenarios at the acceptance repetition count;
reserve it for baseline, GPose-exit/harness changes, final program acceptance, or
a failed/ambiguous focused gate.

Validate the persisted result outside the game:

```powershell
pwsh -File tools/Test-PoserLiveRun.ps1 -Path '<artifact-directory>' -Json
```

Accept only script exit 0 and a terminal successful verdict; require
`AcceptanceQualified` when the tranche calls for the full acceptance gate.
Chat text or file existence is not a verdict.

Pure docs, Domain/Application tests, source checks, and pure moves do not deploy.
Native/lifecycle/resource/UI behavior receives a focused 5–10 minute card.
GPose exit, MCDF, spawning, physics patching, or harness changes receive a
15–30 minute stress card. Failures such as corrupt writes, rollback failure,
invalid opcodes, draw exceptions, and unavailable external plugins are tested by
Release fakes/fixtures; the user is not asked to damage normal game state.

If live acceptance fails, stop testing, preserve the failed branch and evidence,
and intentionally redeploy the last accepted artifact from its immutable SHA.
Do not reset, clean, stash, or discard the failed worktree. Fix in new commits and
repeat review. If no trusted accepted artifact exists, stop deployment work.

## Ownership matrix

| Owner | Production writers permitted |
|---|---|
| Startup/native capability construction | One Train 1 tranche at a time |
| Domain pose masks and transform transactions | One Train 1 tranche at a time |
| PoseImportCapture / IK / facial capture / pose history | Train 2 only |
| AutoSave and ordered GPose exit | Train 3 only |
| ActorIntegrationSession / MCDF resources | Train 4 MCDF tranche only |
| ActorSpawnService / companion polling | Train 4 spawn/companion tranches |
| File formats, library, recovery | Train 5 only |
| AnimationRuntimePort / physics patch | Train 6 only |
| Main/split/pop-out/style UI ownership | Train 7 only |
| Large-class extraction and namespace moves | Train 8 only |

Read-only reviewers may run in parallel. No two production or fix tasks edit the
same owner concurrently.

## Release 0 — establish the control

The current tree is dirty and is not yet a baseline. First:

1. reconcile the stale deferred-feature list in `product-and-boundaries.md`, the
   seven/eight-scenario and UI-harness contradictions in `testing.md`, obsolete
   Debug instructions, stale backend-audit claims, PBI statuses and duplicate
   PBI-015 IDs;
2. retain the dated audits and this plan as non-normative snapshots;
3. commit intentional current changes, create a clean baseline branch and
   immutable annotated tag, and record the exact SHA;
4. run the Release build/test commands and disposition all three current
   warnings (fix or explicitly accept);
5. announce and perform one intentional Debug auto-deployment of the reviewed
   baseline;
6. run `/poser test full`, `/poser test status`, validate the artifact with the
   PowerShell reader, and manually verify actor/bone edit, undo/redo, pose file,
   animation pause/reset, presentation reset, one camera/light action, GPose exit
   and plugin reload.

Record observed baseline quirks. Later trains compare to this evidence, not
memory.

## Train 1 — safety foundation

Each item is a separate tranche and candidate range.

1. **Contract-test foundation (no deploy):** add Domain/Application and
   composition fakes for identity, history, transactions, startup and native
   capability failure.
2. **Startup rollback and Gaze:** provider/host cleanup after activation failure;
   partial hook disposal; visible unavailable status. Fault every activation
   step in Release tests. Focused live card: load/reload and ordinary gaze use.
3. **IK allocation and bone-hook health:** fault every allocation/hook step,
   prove all native blocks free, and refuse edits when the central apply hook is
   absent. Deploy separately from startup/Gaze; live card uses
   `/poser test posing.ik-bake --iterations 1`.
4. **PBI-012 masks:** all eight valid masks including `None` through capture,
   commit, cancel, history, undo/redo, copy and import; unknown bits return typed
   failure with no history. Focused live card: disable all propagation, rotate,
   release, undo/redo, reset, then `posing.bone-components`.
5. **Transaction outcome and recovery:** primary plus rollback outcome,
   RecoveryRequired record, and write guard until retry or explicit stale-target
   disposal. Automated failure matrix; focused normal edit/cancel/undo smoke.

Train 1 is accepted when all five tranches are Accepted and no unavailable native
capability can masquerade as a successful edit.

## Train 2 — asynchronous edit ownership

1. **Import receipt plus late-callback invalidation (one atomic tranche):**
   generation-scoped Pending/Applied/RolledBack/Failed/RecoveryRequired UI and
   history result; timed-out/superseded callbacks cannot write.
2. **Pending recovery:** import, IK and facial setup failure/cancel/dispose restore
   captured stacks, IK, speed and command guard or retain a recovery record.
3. **Live-harness safety:** cancellation always performs bounded cleanup;
   startup failure clears `IsRunning`; report writes/validation are atomic; deep
   cleanup compares actor identity/generation, transforms, stacks, animation and
   owned state.

Live card for item 1: successful Pending -> Applied, second import supersedes the
first, actor redraw/replacement cannot receive stale completion, one undo step.
Item 2 tests ordinary cancel/reload. Item 3 deliberately cancels `/poser test
posing.ik-bake --iterations 1`, verifies cleanup and persisted Cancelled/terminal
verdict. Fault-only rollback failures remain automated.

## Train 3 — AutoSave and ordered GPose exit

Train 3 owns lifecycle participation only. It may register Integration as an
ordered participant, but it must not refactor MCDF redraw, directory, cancellation
or shutdown internals; those belong exclusively to Train 4.

1. **AutoSave exit protocol:** make dependencies truly lazy; reserve the final
   snapshot; join/cancel the worker before CleanOnExit; prove capture ordering.
2. **Exit coordinator:** explicit `snapshot -> invalidate pending work -> restore
   owners -> destroy native entities -> clear bindings -> notify UI` phases.
3. **Exit failure semantics:** aggregate phase failures, retain recovery/quarantine
   state, prevent post-teardown callbacks/publication, and define the exact point
   at which UI notification is allowed. Cover capture/restore/destroy failure,
   late callback, repeated exit and disposal.
4. **Participant migration:** model transform, animation, presentation,
   non-MCDF integration ownership, pending captures and AutoSave move one at a
   time while the old path remains as rollback until verified.

Stress card: actor placement and bone pose; owned animation speed/pause/stance;
opacity/tint/wetness; one available Glamourer/Penumbra/Customize+ state; exit;
verify incoming state restored and final autosave contains placement/pose;
re-enter, reload, run `/poser test full`, validate persisted verdict. MCDF and
physics/replay are explicitly excluded from this card. An unavailable external
plugin is recorded `not exercised`, not passed.

## Train 4 — native and external resources

Every item is separate; no “other small fixes” bundle.

1. **MCDF:** redraw barrier before deleting file-backed resources; retryable
   directory ownership; cancellation/bounded shutdown; then filesystem policy
   behind `IMcdfFileBoundary`; extract `McdfTransaction` only after contract
   tests. Stress import/reset/cancel/redraw/exit/reload and verify collection,
   texture and temp-directory cleanup.
2. **Spawn ownership:** generation/address/serial handle, exact revalidation,
   post-create rollback, failed-deletion retry. Mandatory automated slot-reuse
   test; disposable live spawn/despawn/exit card without forcing external native
   failure.
3. **Companion lifecycle:** clone fidelity decision, child-side detach, visible
   pending/timeout/failure, cancellable minion/mount/ornament polling scoped to
   exact actor generation.
4. **Environment holds:** release on territory/logout; targeted automated and
   short zone-change card.
5. **World-light capture:** preserve original visibility; targeted light card.
6. **Default camera retry:** retry after native manager becomes available;
   targeted GPose entry/card.

## Train 5 — persistence and recovery

1. atomic same-directory writes for pose/camera/light with old-file survival;
2. finite/null/quaternion/size/version validation and bounded clipboard/library
   reads;
3. exact matched reset-before-import scope and deterministic alias collisions;
4. library immutable scan generation/cancellation and bounded traversal;
5. visible AutoSave last-success/error and corrupt/future-file recovery status;
6. camera-origin and light attachment/value round-trip correctness that does not
   imply scene relationship persistence.

Use Release fixtures and disposable test directories for corrupt, unwritable and
oversized cases. Deploy only user-visible runtime/UI slices. Focused live card:
overwrite/load pose, origin camera and light round-trip, exit autosave, recovery
status and cleanup. Train 3 already owns final-snapshot worker lifecycle.

## Train 6 — animation and native patch health

Order is mandatory:

1. **Physics patcher isolation first:** pure extraction, expected-byte and
   instruction-boundary/layout validation, fail closed, explicit failed-unpatch
   result. Release fault tests precede any live toggle.
2. **Global physics ownership:** retain owners until successful unfreeze,
   physics-only reset coverage and truthful global UI readout.
3. **Speed hand-back:** resolve before dropping enforcement; no unconditional
   native 1 without ownership; define reset/retry behavior.
4. **Replay semantics and UI:** explicitly resume or preserve pause; display
   global versus selected state honestly.
5. **Expression redraw:** rebuild existing named expression layers after exact
   skeleton replacement and dispose subscriptions.
6. **Lips speed/pause controls:** feature slice only after generic slot ownership
   semantics are accepted.

Each item is a separate candidate. Physics stress is only after item 1 acceptance:
two actors owning freeze, release/reset, dispose/reload while frozen, expected
bytes restored. Other cards use `posing.animation-interference` plus the exact
speed/replay/expression action.

## Train 7 — UI ownership and style

Separate tranches:

1. coordinated attached/detached close and reopen;
2. disposal of dynamic pop-out map/textures/tasks;
3. per-surface pop-out panes, pickers, dialogs, status and actor targets;
4. exception-safe style ledger and restoration of direct style mutation;
5. GraphicalBonePane race/head query moved behind a Game read port;
6. dead overlay/global-toggle state resolved according to one documented product
   decision; settings folder-open failures become visible.

Draw exceptions are Release fault-injection tests, not a user action. Live card:
attached/detached reopen, two pop-outs on different actors, independent pickers
and dialogs, repeated map open/close, and a normal second-plugin visual check.

## Train 8 — structural extraction and DRY

Only after preceding contracts are accepted:

- extract MCDF transaction, physics patcher (if not already completed), pure
  bone-stack/carryover policy, live-test scenario/report classes and pure sidebar
  projection;
- split DI registration by feature and make ordered lifecycle participation
  explicit;
- move remaining native reads toward Game and rename LegacyRuntime namespaces
  one service at a time;
- delete dead events/state, unused fields and superseded prose after final caller
  searches;
- split large UI/runtime classes only along already-tested ownership seams.

Pure moves do not deploy. A native-boundary move receives a focused smoke. Keep
old facades/adapters until every consumer migrates and delete them last.

## Feature-gap disposition ledger

Code-health work is not feature delivery. Baseline creates/links PBIs with these
initial dispositions:

| Feature gap | Disposition and dependency | Definition of done |
|---|---|---|
| Whole-shot scene save/restore | Scheduled after Trains 3–5 as scene-model program | Actors/props/lights/cameras/environment persist atomically with recovery |
| Nearby overworld actors | Scheduled after spawn handles | Exact visible actor can be imported without manual reconstruction |
| Actor/prop/light relationships | Scheduled with scene model after spawn identity | Attach/detach/clone/save/load preserve stable parent/bone relationship |
| Arbitrary/schema IK | Scheduled after Trains 1–2 | Configured non-limb chains solve, cancel and undo safely |
| Camera target relationship | Scheduled with scene model | Stable target follows movement and survives save/load/missing target |
| Model ID ownership | Scheduled after Trains 3 and 5 | Capture/reset/search/metadata restore incoming ID without Glamourer duplication |
| Selected/reference import UI | Scheduled after Train 2 | Exact selected/subtree/reference action has one final result/history patch |
| Recovery/bad-file visibility | Scheduled in Train 5 | Last success/error and corrupt/future status are visible and actionable |
| Library grouping/search/metadata | Scheduled after Train 5 | Groups and metadata are authored and searched, not filename-only |
| Posing keybinds/overlay actions | Scheduled after Train 7 | High-frequency actions are reachable and configurable; no dead UI state |
| Evaluated-pose mirror/bake | Scheduled after Trains 1–2 | Explicit bake/mirror affects visible evaluated pose with undo and warning |
| Companion clone/detach/status | Scheduled in Train 4 | Relationship fidelity and terminal feedback work for all variants |
| Animation Lips controls | Scheduled in Train 6 | Lips speed/pause follows accepted slot ownership semantics |
| Timeline authoring | Rejected product boundary | Poser remains playback/posing, not an animation authoring timeline |
| Glamourer-owned appearance editing | Rejected product boundary | Equipment/customization/dyes/materials/design remain Glamourer-owned |

Runtime evidence for slot reuse, companion variants, MCDF timing, late import,
foreign speed ownership, camera target/light attachment, and bad files belongs to
the linked tranche/PBI. `Not exercised` never counts as pass.

## Program acceptance

The program is Accepted only when:

- every code-health finding is Accepted, linked to a Parked/Superseded PBI with
  rationale, or documented as a non-defect;
- every changed native/lifecycle/async contract has characterization tests and
  required Release fault or live evidence;
- full Release build/tests pass, warnings are removed or explicitly accepted,
  and required live artifacts have successful persisted verdicts;
- no stale generation can mutate a replacement, no pending operation publishes
  after teardown, and exit/reload leaves no Poser-owned actor, patch, temp file or
  untracked recovery obligation;
- every tranche has an independent final full-range review, accepted-head SHA,
  artifact identity, observed test card, and reconciled docs/PBI status;
- every feature-gap row above has a live owner/PBI and current disposition;
- final `/poser test full` is AcceptanceQualified on the exact final reviewed
  head, followed by the baseline manual smoke and plugin reload/unload;
- new discoveries are tracked work, never hidden scope expansion.

## Compact organizer report template

```text
TL;DR: <status>; <next action or blocker>.
Tranche / Luna role: <id> / <effort>
Status: <state>
Range: <base>..<candidate-or-accepted>
Changed: <commits and paths>
Release gates: <exact commands and results/warnings>
Deployment: <N/A or Debug auto-deployed exact SHA after notice>
Test card: <prerequisite, exact command/actions, expected, cleanup>
Observed/evidence: <result and artifact path>
Findings: <accepted/rejected/deferred>
Next owner/action: <one concrete step>
```
