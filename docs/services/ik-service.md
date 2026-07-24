# IIKService (IKService)

**Source:** `PosingCore/Services/IIKService.cs`, `Poser.Game/LegacyRuntime/IKService.cs`

**Purpose:** Solves inverse kinematics on bone chains by calling the game's own native Havok solvers (CCD and TwoJoint) directly, so IK results are byte-identical to what the engine itself would compute. The public API is pointer-free: `SolveIK(IBone, Vector3 target, BoneIKInfo)` resolves the owning `hkaPose*` internally from the bone's skeleton/actor (same resolution path as `Skeleton.GetGameSkeleton`, pose index 0, matching `BonePosingService`). Solver scratch structs are pre-allocated once in 16-byte-aligned native memory for SIMD.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `SolveIK` | `void SolveIK(IBone bone, Vector3 target, BoneIKInfo ikInfo)` | No-op if the service failed to initialize or `ikInfo.Enabled` is false. Resolves the havok pose for `bone`; dispatches to CCD (`ikInfo.SolverType == IKSolverType.CCD`, walks `ikInfo.CCD.Depth` parents up) or TwoJoint (`ikInfo.TwoJoint` selects first/second/end joints and hinge axis). `target` is in model space. |
| `Dispose` | `void Dispose()` | Frees the three aligned native allocations (only if initialized). |

**Events:** none published or consumed.

**Dependencies:**
- Dalamud: `ISigScanner`, `IPluginLog`.
- PosingCore: none injected; consumes `IBone`/`BoneIKInfo` from `Poser.Core`/`Poser.Entities`, `NativeHelpers.AllocateAlignedMemory`.

**Game surface (WATCH — patch-sensitive):**
- **Three sig scans** resolved to raw `delegate* unmanaged` function pointers (not hooks — direct calls into game code):
  - `hkaCCDSolver` constructor: `"E8 ?? ?? ?? ?? 48 8D 43 ?? 48 C7 43"`
  - CCD solve: `"E8 ?? ?? ?? ?? 8B 44 24 ?? 48 8B 5C 24 ?? 48 3B 5C 24"`
  - TwoJoint solve: `"E8 ?? ?? ?? ?? 0F 28 55 ?? 41 0F 28 D8"`
- **Hand-written struct layouts** (private, must match game binary): `hkaCCDSolver` (0x18, vtbl/Iterations/Gain), `CCDIKConstraint` (0x20, StartBone/EndBone/Target at 0x10), `TwoJointIKSetup` (0x82, joint indices, hinge axis, gains, `EndTargetMS` at 0x40, enforce flags at 0x80/0x81).
- **Native struct reads** in `GetHavokPose`: `Character → DrawObject → CharacterBase → Skeleton → PartialSkeletons[bone.PartialId].GetHavokPose(0)`.
- Constructor failure (any scan throws) sets `_initialized = false` and IK is disabled with a warning — the plugin still loads.

**Brio counterpart:** `Brio/Brio/Game/Posing/IKService.cs` — same three signatures, same struct layouts, same "call the game's Havok solvers" approach (both pass a dummy `byte notSure = 0` for unidentified parameters). Notable difference: Brio's `SolveIK` takes a raw `hkaPose*` from the caller (its SkeletonService plumbs the pointer through); PosingCore's July 2026 change moved pose resolution inside the service so no `hkaPose*` appears in any public interface — callers can't pass a mismatched pose/bone pair.

**Known risks:**
- Direct calls into scanned game functions with hand-declared calling
  conventions: a signature that resolves to the wrong function after a patch
  crashes the game rather than failing gracefully. The three signatures plus
  the 0x82-byte `TwoJointIKSetup` layout are native migration risks tracked by
  the focused live harness.
- `SolveTwoJoint` indexes `boneList[options.FirstBone/SecondBone/EndBone]` after only checking `boneList.Count < options.FirstBone` — bad `TwoJointOptions` (e.g. `SecondBone > FirstBone`) can throw `ArgumentOutOfRangeException`.
- Shared pre-allocated solver structs mean `SolveIK` is not reentrant/thread-safe; safe today because it is only called from the framework/hook path.
- `GetBonesToDepth` relies on `IBone.ParentBone` chains crossing partial skeletons correctly; the chain's `BoneIndex` values are assumed to belong to the resolved pose's partial.

**Test coverage:** `GetBonesToDepth` and the enabled/solver-type dispatch guards are headless-testable with fake bones. Everything from pose resolution down (both solvers, struct layouts, sig scans) is in-game-only (docs/process/in-game-verification.md): after each patch verify init succeeds and both CCD and TwoJoint visibly pull a hand chain to a gizmo target without distortion.


## Drag-path wiring (Phase C, 2026-07-18)
IK is applied Brio-style: **live, every frame, during pose application** —
`BonePosingService.ApplyBoneTransform` computes the position target as
`current model position + stored delta` and, when the bone's `BonePoseInfo.IK`
is enabled, calls `SolveIK(bone, target, ik)` instead of writing the
translation (with `EnforceConstraints == false` it writes the raw target
afterwards, matching Brio). Nothing about the chain is persisted: undo/redo
and .pose export stay pure delta operations.

Arming: `GizmoOverlayWindow` sets the dragged bone's IK at drag start from the
session toggle `IEditorState.IkEnabled` (Transform pane switch), using
`BoneIKInfo.CalculateDefault(boneName)` + `Enabled = true` — note the factory
only selects the solver (TwoJoint for `j_te*`/`j_asi_d*`, CCD otherwise) and
does NOT arm the chain itself. Limitations: primary drag bone only (symmetry
pairs keep plain deltas); solve resolves the hkaPose from the bone each call.
Tests: `IKWiringTests` (solver selection, arming semantics); the solve itself
is native Havok → in-game checklist P-C-IK.
