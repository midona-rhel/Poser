using Poser.Application.Posing;
using Poser.Domain.Transforms;
using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Bindings;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

/// <summary>Captures the solved pose, clears captured interactive layers and IK
/// settings, then re-imports the pose inside the apply pass, following Brio's IK
/// bake approach. Each bone's delta uses the basis after parent edits. Named
/// service layers remain active so their contribution is not baked in twice.</summary>
public sealed class IkBakeCapture : IDisposable, IIkBake
{
    /// <summary>Framework ticks a registered batch is given to reach a pass
    /// before the bake gives up and rolls back. The batch normally lands in
    /// the very next pass; this only exists so a skeleton that stops updating
    /// (gpose ends mid-bake, the actor is redrawn) cannot leave the bake
    /// pending — and the chains disarmed — forever.</summary>
    private const int CompletionTimeoutTicks = 60;

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly ITransformRuntimePort _runtime;
    private readonly EditHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    /// <summary>One slot skeleton's share of a bake: the file collection the
    /// snapshot put its bones in, and the exact targets the bake owns on it
    /// (keyed the way the apply pass identifies a bone).</summary>
    private sealed class SlotBake
    {
        public required ISkeleton Skeleton;
        public required Dictionary<string, PoseFile.BoneData> Collection;
        public required Dictionary<(int Partial, int Index), TransformTargetId> Covered;
        public bool Ended;
        public bool Executed;
    }

    private sealed class Bake
    {
        public required long Generation;
        public required TransformTargetId Target;
        public required IActor Actor;
        public required List<SlotBake> Slots;
        /// <summary>Ordered bake targets and their pre-clear states — captured
        /// before mutation for history and attempted rollback.</summary>
        public required List<TransformTargetId> Order;
        public required Dictionary<TransformTargetId, TransformTargetState> Before;
        /// <summary>Enabled chain configurations saved for attempted restoration.</summary>
        public required List<(IBone Bone, IkChainConfig Config)> Chains;
        /// <summary>Targets an action actually appended a stack to.</summary>
        public readonly HashSet<TransformTargetId> Written = new();
        public string? Failure;
        public bool Completing;
    }

    private Bake? _pending;
    private long _generation;

    public IkBakeCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        IBonePosingService posing,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        ITransformRuntimePort runtime,
        EditHistory history,
        TransformGestureService gestures,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _posing = posing;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _runtime = runtime;
        _history = history;
        _gestures = gestures;
        _log = log;
        _posing.TransitiveActionsEnded += OnTransitiveActionsEnded;
    }

    /// <summary>The bake's own status line: "in flight" until the pass has run
    /// the actions and the history entry has landed, then the reason if it
    /// failed, cleared on success. Carries its target so it cannot survive
    /// onto another bone's IK section.</summary>
    public (TransformTargetId Target, string Text)? Note { get; private set; }

    /// <summary>Whether the bake still owns its pending slot.</summary>
    public bool IsPending => _pending != null;

    /// <summary>Whether the endpoint is enabled, resolves to a multi-bone chain,
    /// and has a captured target or authored translation to bake.</summary>
    public bool CanBake(TransformTargetId target)
    {
        if (_pending != null)
            return false;
        if (target.Bone is not { } boneId)
            return false;
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return false;
        if (_posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return false;
        return AffectedBones(endpoint, config).Count > 1 &&
               HasSolveInput(endpoint, config);
    }

    /// <summary>The bones the solver moves for this target — the limb a bake
    /// is about. The bake itself writes the whole skeleton, as Brio's does;
    /// this is what the UI names. Empty when the target is
    /// not an armed, resolvable chain.</summary>
    public IReadOnlyList<BoneId> AffectedChain(TransformTargetId target)
    {
        if (target.Bone is not { } boneId ||
            _bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint } ||
            _posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return Array.Empty<BoneId>();
        return AffectedBones(endpoint, config)
            .Select(bone => _bindings.GetBoneId(bone))
            .OfType<BoneId>().ToArray();
    }

    /// <summary>
    /// Brio's <c>ResetIK</c>, in order and on one tick: export the solved
    /// skeleton, clear the stacks, disarm, register the per-bone import for the
    /// next pass. Ok means the bake is armed and its actions are queued, not
    /// that they have run — <see cref="Note"/> carries the outcome.
    /// </summary>
    public GestureResult Begin(TransformTargetId target)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("IK bake must run on the framework thread.");
        if (_pending != null)
            return GestureResult.Fail("A bake is already running.");
        // A live transform gesture owns these bones right now; baking under
        // it would interleave two writers on the same chain.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (target.Bone is not { } boneId)
            return GestureResult.Fail("IK bake requires a bone target.");
        if (_bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint })
            return GestureResult.Fail(
                $"Bone {boneId.CanonicalName} did not resolve.");
        var actor = endpoint.Skeleton.Actor;
        if (_bindings.GetActorId(actor) is not { } actorId)
            return GestureResult.Fail("This bone's actor has no stable binding.");

        var config = _posing.GetIkConfiguration(endpoint);
        if (config is not { Enabled: true })
            return GestureResult.Fail("This chain is not armed.");

        var affected = AffectedBones(endpoint, config);
        if (affected.Count <= 1)
            return GestureResult.Fail("This chain does not resolve on this skeleton.");
        if (!HasSolveInput(endpoint, config))
            return GestureResult.Fail("This chain has not solved anything yet.");

        var slots = _skeletons.GetSkeletons(actor);
        if (slots.Count == 0)
            return GestureResult.Fail("This actor has no skeleton to bake.");

        // STEP 1 — Brio SkeletonPosingCapability.cs:134-135. Taken HERE, while
        // the chain is still solving: these absolutes ARE the solved pose.
        var snapshot = _poseFiles.CreatePoseFile(slots);

        // Every bone the bake may touch is captured before it touches any of
        // them, so a failure restores the exact pre-bake stacks and a success
        // already holds the Before half of its history entry.
        var bake = new Bake
        {
            Generation = ++_generation,
            Target = target,
            Actor = actor,
            Slots = new List<SlotBake>(slots.Count),
            Order = new List<TransformTargetId>(),
            Before = new Dictionary<TransformTargetId, TransformTargetState>(),
            Chains = new List<(IBone, IkChainConfig)>(),
        };

        foreach (var slot in slots)
        {
            if (CollectionFor(snapshot, slot.Slot) is not { Count: > 0 } collection)
                continue;
            var covered = new Dictionary<(int, int), TransformTargetId>();
            foreach (var bone in slot.Bones)
            {
                if (bone is VirtualBone)
                    continue;
                // The snapshot's own predicate (Brio's export skips these, so
                // its import never sees them either).
                if (bone.IsPartialRoot && !bone.IsSkeletonRoot)
                    continue;
                if (!collection.ContainsKey(bone.BoneName))
                    continue;
                if (_bindings.GetBoneId(bone) is not { } id)
                    continue;
                var boneTarget = TransformTargetId.ForBone(id);
                if (bake.Before.ContainsKey(boneTarget))
                    continue;
                var captured = _runtime.Capture(boneTarget);
                if (!captured.Success || captured.State is not { } state)
                    continue;
                bake.Before[boneTarget] = state;
                bake.Order.Add(boneTarget);
                covered[(bone.PartialId, bone.BoneIndex)] = boneTarget;
            }

            if (covered.Count == 0)
                continue;
            bake.Slots.Add(new SlotBake
            {
                Skeleton = slot,
                Collection = collection,
                Covered = covered,
            });
        }

        if (bake.Slots.Count == 0)
            return GestureResult.Fail("No bone of this actor could be bound for a bake.");
        // The snapshots are ready; pending ownership is assigned after clearing.

        // STEP 2 — clear the authored stacks of every covered bone. Named
        // service layers stay: see the class remarks.
        foreach (var slot in bake.Slots)
        {
            var poseInfo = _posing.GetPoseInfo(slot.Skeleton);
            foreach (var bone in slot.Skeleton.Bones)
            {
                if (bone is VirtualBone ||
                    !slot.Covered.ContainsKey((bone.PartialId, bone.BoneIndex)))
                    continue;
                poseInfo
                    .GetPoseInfo(bone.BoneName, bone.PartialId)
                    .RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());
            }
        }

        // STEP 3 — IK back to defaults on every chain of every slot.
        foreach (var slot in bake.Slots)
        {
            foreach (var bone in slot.Skeleton.Bones)
            {
                if (bone is VirtualBone)
                    continue;
                if (_posing.GetIkConfiguration(bone) is { Enabled: true } armed)
                    bake.Chains.Add((bone, armed));
            }
            _posing.ClearIkConfigurations(slot.Skeleton);
        }

        // STEP 4 — register the per-bone import for the next pass.
        _pending = bake;
        foreach (var slot in bake.Slots)
        {
            var scope = slot;
            _posing.RegisterTransitiveAction(
                scope.Skeleton,
                (bone, poseInfo) => ApplyBone(bake, scope, bone, poseInfo));
        }

        Note = (target, "Baking the solved limb into the pose…");
        _framework.RunOnTick(
            () => OnTimeout(bake.Generation),
            delayTicks: CompletionTimeoutTicks);
        return GestureResult.Ok();
    }

    /// <summary>
    /// Brio's <c>PoseImporter.ApplyBone</c> (Game/Posing/PoseImporter.cs:9-87),
    /// running inside the apply pass. The bone's own slot collection supplies
    /// the absolute; the basis is <c>bone.LastRawTransform</c> exactly as the
    /// pass has just refreshed it; the delta is appended as a new stack that
    /// the pass applies immediately.
    ///
    /// Brio's near-identity early-out (PoseInfo.cs:89-90) is ported with it: a
    /// bone whose snapshot already matches its basis must not gain a stack, or
    /// every bone of the skeleton would come out of a bake marked as edited.
    /// </summary>
    private void ApplyBone(
        Bake bake,
        SlotBake slot,
        IBone bone,
        BonePoseInfo poseInfo)
    {
        try
        {
            if (!slot.Covered.TryGetValue(
                    (bone.PartialId, bone.BoneIndex), out var boneTarget))
                return;
            if (!slot.Collection.TryGetValue(bone.BoneName, out var fileBone))
                return;

            var desired = _posing.ToApplySpace(bone, fileBone);
            var basis = bone.LastRawTransform;
            if (TransformMath.IsApproximatelyIdentityDelta(BonePoseInfo.Diff(desired, basis)))
                return;

            // Brio passes TransformComponents.All as the new stack's
            // propagation (PoseImporter.cs:35 → PoseInfo.Apply's `propagation`
            // argument), so a baked bone carries its children with it exactly
            // as the pose it replaced did.
            // A bone with no usable basis (a chain link the game left at a
            // zero rotation) cannot take a delta; it is left as it is and
            // the rest of the bake lands.
            if (poseInfo.Apply(desired, basis, TransformComponents.All) == null)
            {
                _log.Warning($"IK bake: {bone.BoneName} produced a non-finite delta and was left out.");
                return;
            }
            bake.Written.Add(boneTarget);
        }
        catch (Exception ex)
        {
            // A throw here is inside the physics detour; swallow it into the
            // bake's own failure so the pass stays intact and the whole edit
            // rolls back on completion.
            bake.Failure ??= $"{bone.BoneName}: {ex.Message}";
        }
    }

    /// <summary>Raised from the native hooks when the interval that owned a
    /// batch ends. Records only — the completion itself needs the framework
    /// thread.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        if (_pending is not { } bake)
            return;
        var complete = true;
        var known = false;
        foreach (var slot in bake.Slots)
        {
            if (ReferenceEquals(slot.Skeleton, outcome.Skeleton) && !slot.Ended)
            {
                slot.Ended = true;
                slot.Executed = outcome.Executed;
                known = true;
            }
            if (!slot.Ended)
                complete = false;
        }
        if (!known || !complete || bake.Completing)
            return;
        bake.Completing = true;
        _framework.RunOnTick(() => Complete(bake.Generation));
    }

    private void OnTimeout(long generation)
    {
        if (_pending is not { } bake || bake.Generation != generation)
            return;
        bake.Failure ??= "The bake never reached an apply pass.";
        Complete(generation);
    }

    /// <summary>
    /// The framework-thread half: by now the pass has run the actions and every
    /// baked stack is in place. Capture the after-states of what actually
    /// changed and append ONE history entry — or, on any failure, restore every
    /// captured target and re-arm the chains as best effort, appending nothing.
    /// </summary>
    private void Complete(long generation)
    {
        if (_pending is not { } bake || bake.Generation != generation)
            return;
        _pending = null;

        var failure = bake.Failure;
        if (failure == null)
        {
            foreach (var slot in bake.Slots)
            {
                if (!slot.Executed)
                {
                    failure = "The apply pass never ran the bake.";
                    break;
                }
            }
        }

        if (failure == null)
        {
            failure = AppendHistory(bake);
            if (failure == null)
            {
                Note = null;
                return;
            }
        }

        _log.Warning($"IK bake failed: {failure}");
        Note = (bake.Target, $"Bake: {failure}");
        Rollback(bake);
    }

    /// <summary>
    /// One undoable entry covering exactly the bones the bake changed: those
    /// whose authored stacks it cleared, and those an action wrote. A bone that
    /// had nothing and received nothing is not part of the edit and is left out
    /// of the patch.
    /// </summary>
    private string? AppendHistory(Bake bake)
    {
        var before = new List<TransformTargetState>();
        var after = new List<TransformTargetState>();
        foreach (var boneTarget in bake.Order)
        {
            var state = bake.Before[boneTarget];
            // HasOverride is set by the capture to "this bone had authored
            // layers", i.e. exactly the bones step 2 cleared.
            if (!state.HasOverride && !bake.Written.Contains(boneTarget))
                continue;
            var captured = _runtime.Capture(boneTarget);
            if (!captured.Success || captured.State is not { } current)
                return captured.Detail ?? $"Could not capture {boneTarget}.";
            before.Add(state);
            after.Add(current);
        }

        if (before.Count == 0)
            return "The bake changed nothing.";

        // One step, one inverse: undoing the bake puts the bones back AND
        // re-arms the chains it disarmed; redoing it bakes the bones and
        // disarms them again.
        var slots = bake.Slots.Select(slot => slot.Skeleton).ToArray();
        _history.Append(new JournalStep(
            "Bake IK",
            () => RestoreStates(before) && RearmChains(bake),
            () =>
            {
                if (!RestoreStates(after))
                    return false;
                foreach (var skeleton in slots)
                    _posing.ClearIkConfigurations(skeleton);
                return true;
            })
        {
            AffectedEntities = before.Concat(after)
                .Select(state => EditHistory.EntityOf(state.Target.ToSelectionId())).Distinct().ToArray(),
        });
        return null;
    }

    private bool RestoreStates(IReadOnlyList<TransformTargetState> states)
    {
        bool landed = true;
        foreach (var state in states)
        {
            var restored = _runtime.Restore(state);
            if (!restored.Success)
            {
                _log.Warning($"IK bake step: {restored.Detail ?? state.Target.ToString()}");
                landed = false;
            }
        }
        return landed;
    }

    /// <summary>Puts back every captured stack and re-arms every chain the bake
    /// disarmed. Restore failures are logged; this is a best-effort rollback.</summary>
    private void Rollback(Bake bake)
    {
        foreach (var boneTarget in bake.Order)
        {
            try
            {
                var restored = _runtime.Restore(bake.Before[boneTarget]);
                if (!restored.Success)
                    _log.Warning(
                        $"IK bake rollback: {restored.Detail ?? boneTarget.ToString()}");
            }
            catch (Exception ex)
            {
                _log.Warning($"IK bake rollback threw for {boneTarget}: {ex.Message}");
            }
        }

        RearmChains(bake);
    }

    /// <summary>Re-arms every chain the bake disarmed, on the bones that
    /// still bind. True when every bound chain took its configuration.</summary>
    private bool RearmChains(Bake bake)
    {
        bool armed = true;
        foreach (var (bone, config) in bake.Chains)
        {
            if (_bindings.GetBoneId(bone) is null)
                continue;
            if (_posing.SetIkConfiguration(bone, config) is { } rearm)
            {
                _log.Warning($"IK bake could not re-arm {bone.BoneName}: {rearm}");
                armed = false;
            }
        }
        return armed;
    }

    /// <summary>The per-slot collection a snapshot puts a slot's bones in —
    /// the same mapping <see cref="IPoseFileService.CreatePoseFile"/> writes
    /// through, and Brio's slot switch in <c>PoseImporter.ApplyBone</c>.</summary>
    private static Dictionary<string, PoseFile.BoneData>? CollectionFor(
        PoseFile poseFile,
        PoseSlot slot) => slot switch
        {
            PoseSlot.Character => poseFile.Bones,
            PoseSlot.MainHand => poseFile.MainHand,
            PoseSlot.OffHand => poseFile.OffHand,
            PoseSlot.Prop => poseFile.Prop,
            PoseSlot.Ornament => poseFile.Ornament,
            _ => null,
        };

    /// <summary>
    /// The bones the solver actually moves. Two Joint moves the resolved
    /// definition (joints, optional twists, endpoint); CCD moves the endpoint
    /// and its parents to the configured depth, so its set is the walked
    /// hierarchy — mirroring <c>IKService.GetBonesToDepth</c> — not the
    /// definition. Ordered root-first, deduplicated.
    /// </summary>
    internal static List<IBone> AffectedBones(IBone endpoint, IkChainConfig config)
    {
        if (config.Solver == IkSolver.Ccd)
            return IkChainShapes.NativeIkMembers(endpoint, config);
        var result = new List<IBone>();
        void Add(IBone? bone)
        {
            if (bone == null || result.Contains(bone))
                return;
            result.Add(bone);
        }

        if (config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control })
        {
            foreach (var member in control.Bones)
                Add(endpoint.Skeleton.Bones.FirstOrDefault(b => b.BoneName == member.Name && b.PartialId == member.Partial));
            return result;
        }
        if (config.Solver != IkSolver.TwoJoint)
        {
            var walked = new List<IBone> { endpoint };
            var current = endpoint.ParentBone;
            while (current != null && walked.Count < config.CcdDepth + 1)
            {
                walked.Add(current);
                current = current.ParentBone;
            }
            for (var i = walked.Count - 1; i >= 0; i--)
                Add(walked[i]);
            return result;
        }

        if (IkChains.ForEndpoint(endpoint.BoneName) is not { } definition)
            return result;
        // Same skeleton, same partial as the endpoint — never another slot.
        var skeleton = endpoint.Skeleton as Skeleton;
        IBone? ByName(string? name) => name == null
            ? null
            : skeleton?.GetBoneByName(name, endpoint.PartialId);
        Add(ByName(definition.FirstJoint));
        Add(ByName(definition.FirstTwist));
        Add(ByName(definition.SecondJoint));
        Add(ByName(definition.SecondTwist));
        Add(endpoint);
        return result;
    }

    /// <summary>Captured targets solve even without an authored translation.</summary>
    private bool HasSolveInput(IBone endpoint, IkChainConfig config) =>
        config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null } ||
        config.ActorAnchor != null ||
        config.TargetMode != IkTargetMode.Actor ||
        _posing.GetModification(endpoint) is { } modification &&
        modification.Position != System.Numerics.Vector3.Zero;

    /// <summary>A pending bake never outlives the session: its registered
    /// completion tracking is dropped here. This method does not roll back
    /// changes or invalidate callbacks already registered with the posing service.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
        Note = null;
    }
}
