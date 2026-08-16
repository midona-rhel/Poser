# Known Brio bugs (do-not-copy list)

> **Created 2026-08-10.** `docs/brio/` held only `parity-checklist.md`; this file did not exist
> on any branch, so it starts here rather than being appended to. Same conventions as the
> checklist: every claim carries a reference call site in the bundled read-only Brio clone, and
> "Poser" states what this codebase does instead.
>
> Scope: behaviours in Brio that Poser deliberately does NOT reproduce. A row here is a decision,
> not a wishlist — an audit that finds Poser diverging from Brio at one of these points has found
> the intended behaviour, not drift. Divergences that are merely *unimplemented* belong in
> `parity-checklist.md`, not here.

## Pose import

### 1. Expression phase 2 pops a blind `j_kao` stack

**Brio:** after the deferred expression pass, `ImportPose_Internal` looks up `j_kao`, takes its
`BonePoseInfo` and calls `RemoveLastStack()` with no check of what that stack IS
(`Capabilities/Posing/PosingCapability.cs:238-247`). The stack it removes is only the import's own
head stack when nothing else has been pushed since; a head edit the user authored between the two
phases — or an earlier authored head offset that the import never added to, because
`PoseInfo.Apply` reuses the last stack when the propagation/IK key matches
(`Game/Posing/PoseInfo.cs:179-192`) — is what gets deleted instead. The user's head edit
disappears with no undo entry.

**Poser:** the pop is gated on the import having authored the stack in the first place.
`PoseImportCapture` records the pre-import head absolute at arm time (`:284-291`) and, in the head
restore, pops only instances that appear in the import's own `Written` set — an instance the apply
stage skipped as near-identity gained no stack and loses none
(`Poser.Game/Posing/PoseImportCapture.cs:692-704`). Import writes are `forceNewStack: true`
(`:414-422`), so the popped entry is the import's phase-1 write and nothing else, and
`RemoveLastInteractiveStack` steps over named service layers
(`PosingCore/Core/BonePoseInfo.cs:149-163`). The pre-import absolute is then re-applied as a
position-only restore rather than trusted to the pop alone.

### 2. `UndoStackSize = 0` skips expression phase 2 entirely

**Brio:** `Snapshot` returns early when `Posing.UndoStackSize <= 0`
(`Capabilities/Posing/PosingCapability.cs:298-303`) — and phase 2 of the expression import is
dispatched from inside `Snapshot`'s `asExpression` branch (`:306-312`). Turning the undo stack off
therefore leaves an expression import stopped after phase 1: the head is still sitting at the
FILE's head transform and the face has never been reconciled against it. The setting reads as a
history preference and silently changes what an import does.

**Poser:** the phases are a stage machine on `PoseImportCapture` (`ImportStage.Apply →
HeadRestore → Reconcile`), each hop scheduled directly on the framework tick; history is touched
once, at the end, by `AppendHistory` for the converged state
(`Poser.Game/Posing/PoseImportCapture.cs:879-934`). No history setting can shorten an import, and
Poser has no undo-depth setting at all.

### 3. Identity stacks are authored for no-op imports

**Brio:** `BonePoseInfo.Apply` allocates its stack entry through `GetTransformIndex` BEFORE it
knows the result is meaningful (`Game/Posing/PoseInfo.cs:94-111` → `:179-194`): the early return
for an identity delta happens at `:100`, but the masked-to-nothing case at `:110` returns only
after `GetTransformIndex` has already appended an identity stack. An import whose delta is a no-op
once the component mask is applied — a rotation-only import of a file that differs from the pose
only in position, say — leaves an empty stack per bone on the bone's stack list, which then
changes what a later `RemoveLastStack` (see row 1) pops.

**Poser:** the import path takes the near-identity early-out CALLER-side, on the already-masked
delta, before any stack is touched (`Poser.Game/Posing/PoseImportCapture.cs:406-409`) — a bone
whose in-scope components already match its basis keeps the stack list it had. That ordering is
what makes row 1's pop safe.

### 4. Speed restore is scheduled before the deferred expression work finishes

**Brio:** `StopSpeedAndResetTimeline` invokes `postStopAction` and then, when
`resetSpeedAfterAction` is set, schedules `SetOverallSpeedOverride(oldSpeed)` two ticks later
(`Capabilities/Actor/ActionTimelineCapability.cs:167-175`). The import that `postStopAction` starts
is not finished at that point: it schedules its own snapshot `+4` ticks out
(`PosingCapability.cs:249-250`), which is where the expression phase-2 chain begins. The restore
therefore lands mid-chain and the animation can resume while the face is still being reconciled —
the pose applies against a moving basis.

**Poser:** the restore is completion-driven. `CleanPoseFacade.BeginImport` hands the capture an
`onFinished` callback and only then schedules the speed restore (+2 ticks, matching Brio's settle
delay) — the actor cannot resume before the import, expression phases included, has reported done.

## Legacy `.cmp` import

### 5. Scale-only `.cmp` bones get a zero quaternion, and zero positions can apply

**Brio:** `CMToolPoseFile.StringToBone` builds a fresh `PoseFile.Bone` and fills in only the
components the file names (`Files/CMToolPoseFile.cs:572-599`). A bone that carries a scale but no
rotation therefore keeps `default(Quaternion)` — `(0,0,0,0)`, which is not a rotation — and the
`.cmp` format carries no positions at all, so every upgraded bone also holds a zero position. Both
reach the importer as ordinary values: the zero quaternion collapses the bone, and the zero
positions apply whenever the popup's Position toggle happens to be on, since nothing on the
`.cmp` path masks it off (`UI/Controls/Stateless/FileUIHelpers.cs:690-691` forwards a null
component override, which leaves `DefaultCMPImporterOptions`' rotation-only mask in charge only
for the typed path).

**Poser:** `CMToolPoseFile.StringToBone` seeds `Rotation = Quaternion.Identity`
(`PosingCore/Files/CMToolPoseFile.cs:490-511`), and `PoseFileService.BuildImportPlan` clamps the
component mask to `Rotation | Scale` for the whole `.cmp` path regardless of options
(`PosingCore/Files/PoseFileService.cs:120-134`), so a `.cmp` can never teleport a bone to the
origin. Both are pinned by `PosingCore.Tests/Files/CmpImportDeviationTests.cs`.
