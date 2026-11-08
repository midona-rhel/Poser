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

/// <summary>Turns one chain's solved bones into ordinary pose edits.
/// Other chains and named service layers keep running.</summary>
public sealed class IkBakeCapture : IDisposable, IIkBake
{
    /// <summary>Stop waiting if the actor disappears or stops updating during the bake.</summary>
    private const int CompletionTimeoutTicks = 60;

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly IPoseFileService _poseFiles;
    private readonly ITransformRuntimePort _runtime;
    private readonly EditHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    /// <summary>The saved pose and bone IDs for the selected chain.</summary>
    private sealed class SlotBake
    {
        public required ISkeleton Skeleton;
        public required Dictionary<string, PoseFile.BoneData> Collection;
        public required Dictionary<(int Partial, int Index), TransformTargetId> Covered;
        public bool Executed;
    }

    private sealed class Bake
    {
        public required long Generation;
        public required TransformTargetId Target;
        public required IkChainConfig Config;
        public required SlotBake Slot;
        /// <summary>Bone edits saved before baking, in application order.</summary>
        public required List<TransformTargetId> Order;
        public required Dictionary<TransformTargetId, TransformTargetState> Before;
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
        IPoseFileService poseFiles,
        ITransformRuntimePort runtime,
        EditHistory history,
        TransformGestureService gestures,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _posing = posing;
        _poseFiles = poseFiles;
        _runtime = runtime;
        _history = history;
        _gestures = gestures;
        _log = log;
        _posing.TransitiveActionsEnded += OnTransitiveActionsEnded;
    }

    /// <summary>Progress or failure text for the selected bone. Cleared when baking succeeds.</summary>
    public (TransformTargetId Target, string Text)? Note { get; private set; }

    /// <summary>True until the bake finishes or times out.</summary>
    public bool IsPending => _pending != null;

    /// <summary>True when the selected bone has an enabled chain with at least two bones.</summary>
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
        return AffectedBones(endpoint, config).Count > 1;
    }

    /// <summary>The bones changed by baking the selected chain.</summary>
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
    /// Captures the selected chain, disables it, and applies its saved pose on
    /// the next evaluation. Note reports when the bake finishes or fails.
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
        var config = _posing.GetIkConfiguration(endpoint);
        if (config is not { Enabled: true })
            return GestureResult.Fail("This chain is not enabled.");

        var affected = AffectedBones(endpoint, config);
        if (affected.Count <= 1)
            return GestureResult.Fail("This chain does not resolve on this skeleton.");
        var slot = endpoint.Skeleton;
        var snapshot = _poseFiles.CreatePoseFile([slot], affected.Contains);
        if (CollectionFor(snapshot, slot.Slot) is not { Count: > 0 } collection)
            return GestureResult.Fail("The chain has no pose data to bake.");

        var bake = new Bake
        {
            Generation = ++_generation,
            Target = target,
            Config = config,
            Slot = new SlotBake
            {
                Skeleton = slot,
                Collection = collection,
                Covered = new(),
            },
            Order = new(),
            Before = new(),
        };

        // Capture every included bone before changing the chain, so Undo can restore it.
        foreach (var bone in affected)
        {
            if (!collection.ContainsKey(bone.BoneName))
                continue;
            if (_bindings.GetBoneId(bone) is not { } id)
                return GestureResult.Fail($"{bone.BoneName} is no longer available.");
            var boneTarget = TransformTargetId.ForBone(id);
            var captured = _runtime.Capture(boneTarget);
            if (!captured.Success || captured.State is not { } state)
                return GestureResult.Fail(captured.Detail ?? $"Could not capture {bone.BoneName}.");
            bake.Before.Add(boneTarget, state);
            bake.Order.Add(boneTarget);
            bake.Slot.Covered.Add((bone.PartialId, bone.BoneIndex), boneTarget);
        }
        if (bake.Order.Count == 0)
            return GestureResult.Fail("The chain has no bones available to bake.");
        if (_posing.SetIkConfiguration(endpoint, config with { Enabled = false }) is { } disableError)
            return GestureResult.Fail(disableError);

        // Replace only this chain's manual edits. Named service layers remain active.
        var poseInfo = _posing.GetPoseInfo(slot);
        foreach (var bone in affected)
            if (bake.Slot.Covered.ContainsKey((bone.PartialId, bone.BoneIndex)))
                poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId)
                    .RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());

        _pending = bake;
        _posing.RegisterTransitiveAction(slot,
            (bone, info) => ApplyBone(bake, bake.Slot, bone, info));

        Note = (target, "Baking the solved limb into the pose…");
        _framework.RunOnTick(
            () => OnTimeout(bake.Generation),
            delayTicks: CompletionTimeoutTicks);
        return GestureResult.Ok();
    }

    /// <summary>Applies a saved bone transform against the raw pose refreshed by this evaluation.</summary>
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
            var fileBone = slot.Collection[bone.BoneName];
            var desired = _posing.ToApplySpace(bone, fileBone);
            var basis = bone.LastRawTransform;
            if (TransformMath.IsApproximatelyIdentityDelta(BonePoseInfo.Diff(desired, basis)))
                return;

            // Propagate the baked transform to children, as an ordinary full-pose import does.
            if (poseInfo.Apply(desired, basis, TransformComponents.All) == null)
            {
                _log.Warning($"IK bake: {bone.BoneName} produced a non-finite delta and was left out.");
                return;
            }
            bake.Written.Add(boneTarget);
        }
        catch (Exception ex)
        {
            // Report the error after leaving the native callback, then restore the pose.
            bake.Failure ??= $"{bone.BoneName}: {ex.Message}";
        }
    }

    /// <summary>Schedules completion on the framework thread after the native pose callback ends.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        if (_pending is not { } bake)
            return;
        if (!ReferenceEquals(bake.Slot.Skeleton, outcome.Skeleton) || bake.Completing)
            return;
        bake.Slot.Executed = outcome.Executed;
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

    /// <summary>Records the completed bake as one undo step, or restores the original pose on failure.</summary>
    private void Complete(long generation)
    {
        if (_pending is not { } bake || bake.Generation != generation)
            return;
        _pending = null;

        var failure = bake.Failure;
        if (failure == null && !bake.Slot.Executed)
            failure = "The apply pass never ran the bake.";

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

    /// <summary>Records changed bone edits together with the chain being disabled.</summary>
    private string? AppendHistory(Bake bake)
    {
        var before = new List<TransformTargetState>();
        var after = new List<TransformTargetState>();
        foreach (var boneTarget in bake.Order)
        {
            var state = bake.Before[boneTarget];
            // Include bones whose previous edits were cleared, as well as newly written bones.
            if (!state.HasOverride && !bake.Written.Contains(boneTarget))
                continue;
            var captured = _runtime.Capture(boneTarget);
            if (!captured.Success || captured.State is not { } current)
                return captured.Detail ?? $"Could not capture {boneTarget}.";
            before.Add(state);
            after.Add(current);
        }

        // Undo restores both the bone edits and the enabled chain.
        _history.Append(new JournalStep(
            "Bake IK",
            () => RestoreStates(before) && SetChainEnabled(bake, true),
            () => RestoreStates(after) && SetChainEnabled(bake, false))
        {
            AffectedEntities = before.Concat(after)
                .Select(state => EditHistory.EntityOf(state.Target.ToSelectionId()))
                .Append(EditHistory.EntityOf(bake.Target.ToSelectionId())).Distinct().ToArray(),
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

    /// <summary>Restores the captured bone edits and enables the selected chain again.</summary>
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

        SetChainEnabled(bake, true);
    }

    /// <summary>Restores the selected chain's settings with the requested Enabled value.</summary>
    private bool SetChainEnabled(Bake bake, bool enabled)
    {
        if (bake.Target.Bone is not { } id || _bindings.Resolve(id) is not { Success: true, Value: { } bone })
            return false;
        var failure = _posing.SetIkConfiguration(bone, bake.Config with { Enabled = enabled });
        if (failure != null) _log.Warning($"IK bake: {failure}");
        return failure == null;
    }

    /// <summary>Selects the pose-file section for the skeleton's slot.</summary>
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

    /// <summary>Returns the bones moved by the selected solver, including Two Joint twist bones.</summary>
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
            return result;

        if (IkChains.ForEndpoint(endpoint.BoneName) is not { } definition)
            return result;
        // Named joints belong to the endpoint's skeleton partial.
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

    /// <summary>Removes the completion listener when the plugin unloads.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
        Note = null;
    }
}
