# IBonePosingService (BonePosingService)

**Source:** `PosingCore/Services/IBonePosingService.cs`, `Poser.Game/LegacyRuntime/BonePosingService.cs`

**Purpose:** The core posing engine. Stores per-bone transform *deltas* ("stacks", accumulated in `SkeletonPoseInfo`/`BonePoseInfo`) and re-applies them into the game's Havok poses every frame from inside two game hooks, following Brio's apply pattern: apply stacks with per-bone `LastRawTransform` + `LastTransform` capture → full cache update → reparent partial-skeleton roots → full cache update → final displayed-transform snapshot (`FinalizeSkeletons`). Also provides reset, flip, and mirror operations. Bones rotate around themselves through delta composition.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `GetPoseInfo` | `SkeletonPoseInfo GetPoseInfo(ISkeleton)` | Gets/creates the delta store for a skeleton (keyed by actor address). |
| `ApplyTransform` | `void ApplyTransform(IBone, Transform newTransform, Transform originalTransform)` | Computes the delta from original and pushes it onto the bone's stack; publishes `BoneTransformChangedEvent`. Ignores `VirtualBone`. |
| `ResetBone` | `void ResetBone(IBone)` | Clears the bone's stacks; publishes the event. |
| `ResetSkeleton` | `void ResetSkeleton(ISkeleton)` | Clears all stacks for the skeleton. |
| `HasModifications` | `bool HasModifications(IBone)` | Whether the bone has any stacks. |
| `GetModification` | `Transform? GetModification(IBone)` | Combined delta of all stacks (positions/scales summed, rotations multiplied+normalized), or null. |
| `RegisterSkeletonForCacheUpdate` | `void RegisterSkeletonForCacheUpdate(ISkeleton)` | Requests a `LastTransform` cache refresh this frame for skeletons with visible overlays/gizmos even without modifications (set is cleared every framework tick, so callers re-register per frame). |
| `TryGetEvaluationObservation` | `bool TryGetEvaluationObservation(IBone, out BoneEvaluationObservation)` | Reads the latest native-hook observation containing the engine animation baseline, evaluated transform, combined applied delta, stack count, and evaluation sequence. Diagnostic only; it cannot mutate pose state. |
| `SnapshotSkeleton` | `void SnapshotSkeleton(ISkeleton)` | Adds the skeleton to this frame's update set, freezing all bones (incl. gaze bones) at current transforms. |
| `FlipBone` | `void FlipBone(IBone)` | Replaces the bone's stacks with a rotation flip (euler X = 180 − X, Y = −Y), Brio-style. |
| `MirrorPose` | `void MirrorPose(ISkeleton)` | Snapshots and exchanges left/right stack lists within the same partial, inverting additive position/scale and conjugating rotation. Preserves propagation and named layers. |
| `GetMirrorBoneName` | `string? GetMirrorBoneName(string)` | Swaps `_l`/`_r` suffix, null if neither. |
| `Dispose` | `void Dispose()` | Disposes both hooks, unsubscribes, clears state. |

**Events:**
- **Published:** `BoneTransformChangedEvent(IBone)` — from `ApplyTransform`, `ResetBone`, `FlipBone`, and both sides touched by `MirrorPose`.
- **Consumed:** `GPoseStateChangedEvent` — on exit, clears all pose infos and update sets.

**Dependencies:**
- Dalamud: `IPluginLog`, `IFramework`, `IGameInteropProvider`, `ISigScanner`.
- PosingCore: `IGPoseService`, `ISkeletonService`, `IActorManager`, `IEventBus`.

**Game surface (WATCH — patch-sensitive):**
- **Sig scan + hook** `UpdateBonePhysics`: `"48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4"` — detour runs original, then re-applies all bone transforms while in GPose (`_isUpdating` guard against reentry).
- **Sig scan + hook** `FinalizeSkeletons`: `"40 53 57 41 54 41 55 48 83 EC ?? ?? 48 ?? ?? ?? ?? ?? ?? ?? 4C"` — after original, takes the final `LastTransform` snapshot for modified + overlay-registered skeletons.
- **Native structs:** `Character → GameObject.DrawObject → CharacterBase → Render.Skeleton` (`PartialSkeletonCount`, `PartialSkeletons[i].GetHavokPose(0)`); havok `hkaPose.AccessBoneModelSpace(idx, Propagate/DontPropagate)`, `pose->Skeleton->Bones[i].Name`; raw reinterpret-casts of `Vector3/Quaternion` into `hkVector4f`/`hkQuaternionf`.
- Both hooks are wrapped in try/catch at construction: a failed scan degrades to "posing silently does nothing" rather than blocking load.

**Brio counterpart:** `Brio/Brio/Game/Posing/SkeletonService.cs` (hook, cache, and apply pipeline) and `Brio/Brio/Game/Posing/PoseInfo.cs` (delta stacks and inverse mirror convention). PosingCore reproduces Brio's apply/cache/reparent/cache/finalize sequence deliberately. Differences: PosingCore keys pose infos by actor address and adds optional named stack layers for idempotent services such as expression blending.

**Known risks:**
- Per-frame cost inside a render-path hook: three full skeleton walks with per-bone *string* name lookup (`GetBoneByName`) for every modified skeleton. Fine for a few actors; a scaling hazard for crowd scenes.
- The two sig scans are the most patch-fragile surface in PosingCore alongside `IKService`'s. Failure mode is silent (warning log only) — post-patch in-game check is mandatory.
- `FlipBone` round-trips through Euler space. `PoseMath` exposes literal X/Y/Z
  coordinate axes and internally passes them to
  `CreateFromYawPitchRoll` as `(Y, X, Z)`; the flip convention must remain
  involutive under that mapping.
- Whole-pose mirroring only exchanges existing deltas. It intentionally does not manufacture asymmetry from an unmodified live animation pose.
- Every persistent stack write rejects NaN and positive/negative infinity. This
  is a last-line safety check; orbit math additionally rejects runaway finite
  positions before they reach the stack.
- `_skeletonsToUpdateCache` cleared every framework tick but consumed in `FinalizeSkeletons` — the ordering assumption (framework update before finalize within a frame) is implicit.
- Evaluation observations are model-space diagnostics keyed by current native
  identity. They are intentionally unsuitable for persistence, history, or UI
  ownership and are cleared with the corresponding pose state.

**Test coverage:** `posing.animation-interference` starts a looping animation
without freezing the actor, independently commits production translation,
scale, and rotation gestures, and collects twelve or more distinct
pre-layer/post-layer native observations for each component. Every observation
must equal animated baseline plus the unchanged, component-isolated persistent
delta. The scenario is accepted only after eight successful iterations.
Reparenting, face partials, physics, gaze, and rendered appearance remain
separate live scenarios.
