using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Turns a live-solved IK chain into ordinary pose edits — Brio's "Set IK
/// Changes" button, <c>SkeletonPosingCapability.ResetIK</c>
/// (Capabilities/Posing/SkeletonPosingCapability.cs:132-148), ported
/// mechanism-for-mechanism rather than approximated from outside the apply
/// pass.
///
/// Brio's four steps, and what each becomes here:
///
///   1. <c>ExportSkeletonPose(pose)</c> (SkeletonPosingCapability.cs:68-129)
///      writes <c>bone.LastRawTransform</c> for every bone that is not a
///      non-root partial root into a <c>PoseFile</c>. That cache is written
///      only by the apply pass, so it holds the SOLVED absolutes of the last
///      frame. Poser: <see cref="IPoseFileService.CreatePoseFile"/>, which is
///      that method's parity port, over every present slot.
///
///   2. <c>bonePoseInfo.ClearStacks()</c> for every exported bone, then
///      <c>ResetPose()</c> → <c>PoseInfo.Clear()</c> (PoseInfo.cs:43-49).
///      Poser: <c>RestoreInteractiveStacks(empty)</c> per bone, which clears
///      the authored stacks and KEEPS named service layers — Brio has no such
///      layers, and keeping them is what the mechanism requires: the layer's
///      contribution stays in the pass's basis, so the delta computed in step
///      4 excludes it instead of absorbing a value its owner will re-drive.
///
///   3. <c>DefaultIK = BoneIKInfo.CalculateDefault(name)</c> for every exported
///      bone — IK back to "off, with this bone's solver defaults". Poser:
///      <see cref="IBonePosingService.ClearIkConfigurations"/> per slot, after
///      which <c>GetIkConfiguration</c> reads back
///      <c>IkChainConfig.DefaultsFor(...)</c>, i.e. exactly the same thing.
///      The scope is Brio's, EVERY chain, not just the one baked: Poser stores
///      IK per bone rather than per stack, so an endpoint left armed would push
///      its own freshly baked position delta back through the solver
///      (<c>BonePosingService.ApplyBoneTransform</c>: armed and a non-zero
///      position delta means "solve toward it"), moving a limb nobody touched.
///
///   4. <c>ImportSkeletonPose(pose, ...)</c> (SkeletonPosingCapability.cs:62-66)
///      registers <c>PoseImporter.ApplyBone</c> as a transitive action, and
///      <c>SkeletonService.ApplyBrioTransforms</c> (SkeletonService.cs:89-131)
///      runs it INSIDE the next pass, per bone, after that bone's existing
///      stacks are applied and its caches refreshed, appending a stack the
///      same pass then applies. Poser:
///      <see cref="IBonePosingService.RegisterTransitiveAction"/> and
///      <see cref="ApplyBone"/> below.
///
/// Step 4 is the whole reason this works. <c>BonePoseInfo.Apply(desired,
/// basis)</c> stores <c>desired - basis</c>; the only basis that makes the
/// stack reproduce the snapshot is the one the pass is running on RIGHT THEN —
/// after the clear, after the solver is gone, and after every parent already
/// written in this same pass has moved this bone. Computing it from outside the
/// pass, on any tick, cannot see that state.
/// </summary>
public sealed class IkBakeCapture : IDisposable
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
    private readonly TransformHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly JournalContexts _journal;
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
        /// before anything was written, so a failure restores exactly what was
        /// there and success has a Before half that needs no re-reading.</summary>
        public required List<TransformTargetId> Order;
        public required Dictionary<TransformTargetId, TransformTargetState> Before;
        /// <summary>Chains disarmed by step 3, restored verbatim on failure.</summary>
        public required List<(IBone Bone, IkChainConfig Config)> Chains;
        /// <summary>Targets an action actually appended a stack to.</summary>
        public readonly HashSet<TransformTargetId> Written = new();
        public string? Failure;
        public bool Completing;
        public JournalContexts.StepScope? Journal;
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
        TransformHistory history,
        TransformGestureService gestures,
        JournalContexts journal,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _journal = journal;
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

    /// <summary>Whether a bake is armed and has not finished. True from the
    /// click until the pass has executed the registered actions AND the
    /// history entry has been appended (or the whole thing rolled back).</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Whether this endpoint has a solve worth baking: armed, its chain
    /// resolves, and the runtime's own solve condition holds (a Fixed chain
    /// always holds a captured target; a Relative one needs an authored
    /// translation).
    /// </summary>
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
    /// this is what the UI and the live scenario name. Empty when the target is
    /// not an armed, resolvable chain.</summary>
    public IReadOnlyList<IBone> AffectedChain(TransformTargetId target)
    {
        if (target.Bone is not { } boneId ||
            _bindings.Resolve(boneId) is not { Success: true, Value: { } endpoint } ||
            _posing.GetIkConfiguration(endpoint) is not { Enabled: true } config)
            return Array.Empty<IBone>();
        return AffectedBones(endpoint, config);
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
        // The scope opens BEFORE step 2 resets the stacks.
        bake.Journal = _journal.BeginActorStep([actorId.LogicalId]);

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

            Transform desired = fileBone;
            var basis = bone.LastRawTransform;
            if (IsApproximatelyIdentity(BonePoseInfo.Diff(desired, basis)))
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
    /// captured target, re-arm the chains, and append nothing.
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
            Context = bake.Journal?.Complete(),
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
    /// disarmed. Nothing of the bake survives a failure.</summary>
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

    /// <summary>Brio's <c>Transform.IsApproximatelySame(Transform.Identity)</c>
    /// (Core/Transform.cs:96-101) on a stack delta: position and scale are
    /// additive, rotation multiplicative.</summary>
    private static bool IsApproximatelyIdentity(Transform delta)
    {
        const float tolerance = 0.000001f;
        return MathF.Abs(delta.Position.X) < tolerance &&
               MathF.Abs(delta.Position.Y) < tolerance &&
               MathF.Abs(delta.Position.Z) < tolerance &&
               MathF.Abs(delta.Scale.X) < tolerance &&
               MathF.Abs(delta.Scale.Y) < tolerance &&
               MathF.Abs(delta.Scale.Z) < tolerance &&
               MathF.Abs(MathF.Abs(delta.Rotation.W) - 1f) < tolerance;
    }

    /// <summary>
    /// The bones the solver actually moves. Two Joint moves the resolved
    /// definition (joints, optional twists, endpoint); CCD moves the endpoint
    /// and its parents to the configured depth, so its set is the walked
    /// hierarchy — mirroring <c>IKService.GetBonesToDepth</c> — not the
    /// definition. Ordered root-first, deduplicated.
    /// </summary>
    private static List<IBone> AffectedBones(IBone endpoint, IkChainConfig config)
    {
        var result = new List<IBone>();
        void Add(IBone? bone)
        {
            if (bone == null || result.Contains(bone))
                return;
            result.Add(bone);
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

    /// <summary>The runtime's own per-frame solve condition: a Fixed chain
    /// holds its captured target with or without an authored delta, a
    /// Relative one solves only for a translation.</summary>
    private bool HasSolveInput(IBone endpoint, IkChainConfig config) =>
        config.TargetMode != IkTargetMode.Actor ||
        _posing.GetModification(endpoint) is { } modification &&
        modification.Position != System.Numerics.Vector3.Zero;

    /// <summary>A pending bake never outlives the session: its registered
    /// actions die with the posing interval and the completion it was waiting
    /// for is dropped.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
        Note = null;
    }
}
