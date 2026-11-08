# Poser.Game behavior follow-up

This plan records behavior concerns found during the comment audit. It does not
treat comments or tests as evidence and does not authorize runtime changes.

## Actor transform overrides

Evidence: `TransformRuntimePort.ApplyAbsolute` validates a `PoseTransform`, calls
the void `PosingService.SetTransformOverride`, and reports success. The setter
silently returns when GPose or actor availability checks fail, then clamps every
accepted scale component to `[0.01, 100]`. The domain accepts signed scale with
absolute magnitude `[1e-8, 1000]`.

Replacement:

1. Give `SetTransformOverride` an explicit result and propagate refusal through
   `TransformRuntimePort.ApplyAbsolute` and `Restore`.
2. Remove the contradictory positive-only clamp. Preserve finite authored
   components known to work through Customize+ and negative mirroring; put any
   proven native limit in the shared domain contract.
3. Do not infer arbitrary reflection signs from a composed matrix. Preserve
   authored components where they exist and make non-decomposable matrix paths
   return an explicit failure instead of inventing scale.

Verification: unit-test setter refusal and port propagation; exercise a real
Customize+ near-zero scale and a negative mirrored scale in GPose; confirm the
requested value, stored override, and draw-object value agree. Exact supported
limits remain a live-test question, not a conclusion from source.

The same outcome must flow through `ParentingRuntime.Write`, which currently
returns `true` immediately after the void actor setter. Audit all remaining
setter callers before changing the interface.

## Bone capture fallback

Evidence: `TransformRuntimePort.CaptureBone` retries a rejected live transform
through `Sanitized`, which replaces nonfinite position with zero, nonfinite scale
with one, and invalid rotation with identity. This creates a synthetic undo
baseline without reporting which component was unreadable.

Replacement: distinguish the known transient zero-rotation case from nonfinite
position or scale. Permit the narrow rotation fallback only if live behavior
still requires it; return `InvalidTransform` for nonfinite translation/scale so
the gesture does not promise an invented restoration point.

Verification: port tests for zero rotation, NaN position, and NaN scale, followed
by a live reproduction of the stated mid-blend zero-rotation case before
retaining that exception.

## Matrix decomposition

Evidence: `Transform.FromMatrix` uses unsigned basis-vector lengths and replaces
an exact zero axis with `0.01`. Bone-world, collider-capture, and IK frame paths
call it. A composed matrix does not uniquely identify which authored axis held a
reflection, so generic "sign recovery" would be speculative.

Replacement: avoid decomposition when authored transforms can be composed in
component form. At unavoidable matrix boundaries, report when the requested representation
cannot express the result. Preserve collapsed authored components where available;
only require invertibility at an operation that actually computes an inverse.

Verification: component-preservation tests for the actual callers, plus GPose
checks using existing negative mirroring and near-zero Customize+ inputs.

## Aligned allocation

Evidence: `NativeHelpers.AllocateAlignedMemory` computes
`alignment - (address % alignment)`. An already aligned address therefore moves
forward by a full alignment, but the allocation reserves only
`size + alignment - 1` bytes. `IKService` allocates three native solver structs
with their exact `sizeof` and writes/dispatches those complete structs through
the returned aligned pointers, so the already-aligned case leaves the last byte
outside the reserved extent. All observed callers use alignment 16. Tracked in #637.

Replacement: calculate padding as
`(alignment - remainder) % alignment` (or use the runtime's aligned allocation
API while retaining the original pointer needed for the paired free).

Verification: allocator tests with an injectable base address covering remainder
zero and every remainder below the alignment; assert the aligned range stays
inside the allocated extent. Confirm every caller uses the paired free method.

## Catalog initialization

`AnimationCatalogLoader.EnsureLoaded` and `CompanionCatalogLoader.EnsureLoaded`
start a background build and leave `_started` true after a failure, publishing an
empty catalog. Callers cannot distinguish a failed load from a valid empty result.
Expose the failed state and define a retry action before changing synchronization.
Concurrent entry has not been established from callers; do not add locking solely
because `_started` is a Boolean. First trace scheduling and disposal ownership if
this initialization path changes. Verify the actual failed-load retry behavior.

## World-asset name overrides

Evidence: `WorldAssetCatalog` stores the names path, overrides, and furniture
labels in process-wide static fields. Constructing a second catalog replaces the
path and furniture labels while retaining the first catalog's cached overrides.
The malformed-file catch is also silent, so the user cannot distinguish an empty
file from ignored invalid JSON.

First establish whether production constructs multiple catalog instances. If so,
make all three values instance-owned in the existing catalog and report read/parse
failures there. Do not introduce another store interface solely because a second
test instance demonstrates state sharing. If composition has one instance, record
that fact and assess actual lifetime/reset behavior before changing ownership.

Verification: construct two catalogs with different configuration directories
and furniture data and prove their labels do not cross; cover malformed and
unreadable override files with an observable warning and derived-label fallback.

## Camera restoration outcomes and target identity

Evidence: `CameraControl.ResetProperties` calls `CameraTargetControl.PutTarget`
while replaying history but ignores its result. `ResetState.Apply` then restores
`IsTargetLocked` and the target label even if the target could not be restored.
`PutTarget` returns success without clearing the current target when its saved
actor ID cannot resolve. `CameraControl.SetLive` similarly records success after
the void `VirtualCameraService.SetLive`, which can return without switching when
its native camera pointer is absent.

Replacement: propagate target restoration failure before applying dependent
lock/name state; define missing-target replay as an explicit cleared target.
Give camera switching an observable result and record history only after the
requested camera actually becomes live. Keep missing camera and temporarily
unavailable native camera distinguishable for retry decisions.

Verification: reset a locked followed camera, replace the actor generation, then
undo; assert no stale lock/name or unrelated target remains. Refuse native camera
availability during switching and replay and assert no false success/history.

## Camera tracking math and camera switching

Evidence: `NativeCameraHooks.CalculateLookPositionDetour` normalizes absolute
actor/pivot world positions for FollowAndPan. Zero input produces NaN and equal
relative scenes at different world origins produce different blend factors. The
factor's finite range is 0..1, not the former comment's claimed 0..0.5.
`FreeCameraController` owns one `_freeFollowPivot` across all virtual cameras;
`VirtualCameraService.SetLive` clears only its speed notice. The next tracked
free camera can therefore consume a displacement measured against the previous
camera's pivot.

Replacement: specify the intended FollowAndPan blend in relative coordinates
and check zero/nonfinite inputs before writing camera state. Reset or store
follow-pivot/input accumulation per camera when switching. Verify the intended
interaction against Ktisis before selecting a replacement blend; do not turn the
old formula's description into its specification.

Verification: translated copies of one tracking scene must produce equal relative
camera movement; zero-world-position inputs must remain finite. Switch between
two tracked free cameras with distant pivots and confirm no inherited jump.

## External appearance cleanup and profile probe errors

Evidence: `InvisibleSkinService.Apply` ignores the `IntegrationResult` returned
by `PenumbraIpc.RemoveInvisibleSkinMods` in its finally block. The operation can
report success while the collection still has Poser's material remap.
`PenumbraIpc.DeleteDuplicateCollection` removes the owned GUID from
`DuplicateCollectionLedger` before calling the provider. A failed provider delete
therefore loses the retry identity; the next release reports success because the
ledger is empty. `RestoreInheritedCollection` also ignores cleanup results for
failed setup and a replaced prior collection.

Replacement: retain failed deletions by collection GUID independently of actor
address, report cleanup failure, and retry through the owning lifecycle. Remove
ledger entries only after accepted deletion or confirmed absence. Serialize
invisible-skin temporary mods per collection or use distinct ownership tokens:
all actors in one collection currently share the same mod tag and priority.

Verification: inject each cleanup failure and then retry after the actor is gone;
assert the same GUID remains reachable and gets deleted exactly once. Apply
clothing-only to two actors sharing a collection and interleave completion and
failure; one operation must not remove the other's active remap.

Evidence: `CustomizePlusIpc.ProbeBodyProfile` handles a null active ID before
checking non-success error codes. An error plus null becomes a successful empty
profile. `IntegrationOwnership.CaptureBodyProfile` interprets this as no profile;
`IntegrationSelectors.ApplyBodyProfileJson` and `CheckBodyProfileDisplaceable`
then make decisions using that successful probe.

Replacement: classify the provider error first, handling only the documented
profile-not-found code as absence. Keep the documented API inability to report
temporary profiles separate from errors and from proven absence.

Verification: table-driven provider responses for success, not-found, invalid
character, and unknown errors with null and nonnull IDs; assert capture and apply
callers propagate failure instead of capturing an empty baseline.

## Lighting hook activation

Evidence: `WorldLightCapture.HookDestructor` assigns `_lightDtorHook` before
`Enable()`. If enable throws, the catch leaves the field nonnull.
`HasDestructorHook`, `Candidates`, and `LightingService.CaptureWorldLight` then
use nonnull as proof that destruction is tracked. The constructor hook and
`GPoseCameraLights` follow the same incomplete activation pattern.

Replacement: publish the hook before enabling for detour safety, but dispose and
clear it on failed activation. Expose successful activation separately and admit
world-light pointers only while destructor tracking is active. Decide a bounded
retry policy for failed discovery/activation; `_worldLightsSeeded` currently
becomes true before graph enumeration succeeds.

Verification: inject HookFromAddress and Enable failures separately; assert no
world candidate or capture is admitted without an enabled destructor hook and
no partial hook leaks. Retry failed enumeration on the next permitted attempt.

## Gobo replacement failure

Evidence: `LightGoboController.ApplyGoboPath` clears the current native texture
and recorded path before attempting the new load. A refused load returns false
with the previous gobo already gone. `LightingService.ApplyGobo` returns that
false directly; cloning also ignores the gobo-copy result.

Replacement: retain the previous resource reference/path until the new texture
is accepted, or restore it on failure and report restoration failure separately.
Carry partial clone failure to the owning creation transaction instead of silently
creating a visually different clone.

Verification: start with an applied gobo and inject a rejected new resource;
assert either the old texture remains or a visible partial-failure outcome is
reported. Cover reference counts on successful replace, refusal, and clone cleanup.

## Physics patch failure state

`WorldRenderingService.SetPhysicsFrozen` forwards `PhysicsFreezePatcher.SetFrozen`, which trusts `IsFrozen` for no-op requests and disposal. `Replace` can write successfully and then throw while restoring page protection. The first region replacement is outside the rollback try block; rollback of the first region can also fail. This is a source-level failure path, not a reproduced live symptom. A failure can leave mixed instructions while `IsFrozen` still reports the previous completed state, and disposal skips recovery when that state is false.

Separate observed instruction state (original, patched, mixed, foreign/unreadable) from requested state. After any write/protection failure, inspect both spans and retain recovery ownership until recognized bytes are restored; preserve the original failure and any rollback failure. Never overwrite foreign bytes. Add a targeted test for a permission-restore failure after the first write and confirm retry/disposal retain ownership of changed bytes. The current fake permits only one failed write number and always succeeds at permission changes, so it does not cover these states.

## Facial capture cleanup

`ExpressionPreview.Bake` starts `FacialPoseCapture.Begin`. Begin publishes `_pending` before requesting raw refresh; its catch resumes commands but does not clear that pending operation. Route setup failure through the same pending-operation termination used for cancellation, so a failed start cannot continue on later updates or block retry. A targeted refresh-failure test should assert no pending capture and no later pose write.

`ReleaseFacialDrive` clears its ownership flag before calling `AnimationSession.ClearSlotSpeed`; a refused outcome is only logged. `SpeedAnimationSession.ClearSlotSpeedCore` retains its stored override on refusal, but `FinishOwnedState` returns success. Return that cleanup outcome and keep the release responsibility until it succeeds or the actor/session is explicitly gone; test a refused release followed by retry. The current slot-speed request of 1 is multiplied by overall speed in `AnimationSpeedRuntimePort`, so it does not advance an actor held at 0. Verify paused capture output in-game before choosing a different settling policy.

## Festival queue composition and restoration

Each Add, Remove, or ChangePhase reads `EngineFestivals`, ignoring queued changes. Two additions before the framework drains the queue can choose the same empty slot; the second queued full snapshot then removes the first addition. Build commands against the latest desired slot state, or queue operations rather than stale full snapshots. Verify two additions during a layout transition survive in separate slots.

GPose exit calls `Restore(viaTick: true)`, which enqueues the original slots and immediately clears `_original`. If Dispose runs before the queue drains, its direct restore sees no original and then clears `_pending`, losing the restoration. Keep restoration ownership until applied or transferred to the engine queue; test exit followed immediately by disposal. These are source-level orderings, not reproduced live symptoms.

## IK bake ownership and replay

`PoseInspectorPane` calls `IkBakeCapture.Begin` from its Bake actions (including the deferred next-target action). Begin clears interactive stacks and IK configurations before assigning `_pending`; setup has no exception handler. A failure during clearing can therefore leave mutations without pending recovery. After registration, `ApplyBone` has no cancellation check: timeout rollback clears `_pending` but does not invalidate the queued action. `Rollback` logs restore failures and drops responsibility, while `RearmChains` skips bones that no longer bind. History closures retain skeleton and bone instances rather than resolving their saved identities.

Establish operation ownership before the first clear, invalidate queued callbacks before rollback, and retain incomplete recovery. Save stable target IDs/configurations for replay instead of captured wrappers. Verify one setup failure after a successful clear, timeout followed by a late callback, and undo after a skeleton rebuild. These are demonstrated code paths; live frequency is not established.

## IK identity across skeleton rebuilds

`SkeletonService.GetSkeleton` rebuilds a cached skeleton in place when its layout changes. `BoneApplyPass` deliberately retains actor-slot pose stores through replacement. `IkChainRegistry` keys its native chain and held state by `(SkeletonKey, partial, bone index)`, with no build revision or canonical name, and `BonePosingService` purges only when the actor key disappears. `BoneApplyPass` retrieves that state by the new native index. Thus an index reused for a different bone can receive the old chain configuration and resolved native joint indices; held targets also retain old bone objects.

Separate name-based authored settings from resolved native chain state. On a build revision change, re-resolve settings by stable names and discard or explicitly recapture native indices, held bone references, and collision continuation. Verify a reordered layout with the same actor/slot, and a rebuilt target skeleton. Preserve intended pose persistence while refusing an unresolved chain.

## IK configuration admission

`PoseInspectorPane`'s bone picker dispatches non-FABRIK choices to `IkConfigurationPort.SetBoneTarget`. That path calls `WriteBoneTarget` without checking `ActiveGesture`, unlike general configuration, FABRIK target, and entity-target writes. Route all public target writes through the same gesture-admission rule and verify direct port calls while a gesture is active. The UI timing needed to trigger this is not established; the port contract is inconsistent now.

## Native vector writes and allocation cleanup

`IKService.WriteModelSpace` and several `BoneApplyPass` writes reinterpret a `Vector3` address as `hkVector4f*`. The source is 12 bytes and the read is 16, so the fourth component comes from outside the value. Construct the native vector explicitly with a defined fourth component, after checking the expected Havok convention in the reference implementation. Verify XYZ preservation and the chosen W for translation and scale at these actual write sites.

Separately, `IKService` allocates three buffers in sequence. A failure after the first allocation leaves `_initialized` false; Dispose frees only when it was true, losing cleanup for partial initialization. Track and release each allocated pointer independently in the constructor failure path and Dispose. A targeted second-allocation failure should release the first allocation exactly once. This is separate from allocator issue #637.

## Preview completion and teardown

`PosePreviewService.TryApplyPendingPose` passes no receipt callback to either import overload. It advances the stage and clears file refusal text based on admission alone, even though `PoseImportCapture` can later fail or require recovery. Observe the terminal receipt, keyed to the request serial; only an Applied receipt should establish that stage as displayed. Define whether a refused baseline permits the file stage and present later-stage failure without accepting stale receipts from a replaced request. Verify accepted-then-failed import and a replacement request during completion.

Close schedules `ReleaseCharaView` through an unawaited task. The helper catches synchronous scheduling failure only, and disposal does not wait for release. Coordinate this native owner with the host's framework-thread teardown drain; report an unfinished release rather than treating scheduling as completion. Test a failed dispatch with a fake scheduler, without requiring game fault injection.

## Bone batch completion and display demand

`BoneSnapshotPass.FinalizeSkeletons` returns before `EndTransitiveActions` when `BoneSnapshotDemand.Wanted` is false. `TransitiveActionScheduler` keeps callbacks registered until that end call; `BoneApplyPass` can execute them again on subsequent passes. Raw-transform reads do not request display demand. Decouple batch termination from optional display-cache copying. Verify an import/export batch with all UI readers absent completes once, and that a timed-out bake's callback cannot write after rollback. Caller-specific demand timing remains to be reproduced; the gate currently combines independent responsibilities.

## Reference-pose root selection

`Skeleton.BuildFromGameSkeleton` attaches a single-root partial by connected indices, and a multiple-root partial by namesake. `CaptureReferencePose` instead prefers a partial-0 namesake for every parentless bone, falling back to connected indices. `NativePoseImportService.ApplyReferencePose` and collider capture consume this result. Characterize a single-root partial whose namesake differs from its connected parent; then align reference composition with the actual native attachment rule if they differ. This is an investigation with a concrete fixture, not a claim that current game models exhibit the mismatch.
