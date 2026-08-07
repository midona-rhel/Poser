using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Applies a <see cref="PoseImportPlan"/> INSIDE the apply pass — Brio's
/// interactive pose-file import (SkeletonPosingCapability.ImportSkeletonPose →
/// PoseImporter.ApplyBone, Game/Posing/PoseImporter.cs:9-87), on the same
/// transitive-action lifecycle as <see cref="IkBakeCapture"/>.
///
/// The mechanism is the point: each write's delta is diffed against
/// <c>bone.LastRawTransform</c> exactly as the pass has just refreshed it —
/// after the synchronous reset cleared the scope's stacks, and after every
/// parent already written in this same pass has moved this bone. A basis read
/// on any earlier tick (the replaced ImportEdit path) predates those parent
/// deltas, so children double-move under propagation. Partial-component
/// imports mask the DELTA (Brio PoseImporter.cs:35 → PoseInfo.Apply's applyTo,
/// PoseInfo.cs:108 calc.Filter), so an excluded component contributes nothing
/// instead of pinning the bone to a stale absolute.
/// </summary>
public sealed class PoseImportCapture : IDisposable
{
    /// <summary>Framework ticks a registered batch is given to reach a pass
    /// before the import gives up and rolls back — same guard as
    /// <see cref="IkBakeCapture"/>: a skeleton that stops updating must not
    /// leave the import pending forever.</summary>
    private const int CompletionTimeoutTicks = 60;

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly ITransformRuntimePort _runtime;
    private readonly TransformHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly IkBakeCapture _ikBake;
    private readonly IPluginLog _log;

    /// <summary>One slot skeleton's share of an import: the plan's writes
    /// keyed the way the apply pass identifies a bone.</summary>
    private sealed class SlotImport
    {
        public required ISkeleton Skeleton;
        public required Dictionary<(int Partial, int Index),
            (TransformTargetId Target, Transform File, TransformComponents Components)> Writes;
        public bool Ended;
        public bool Executed;
    }

    private sealed class Import
    {
        public required long Generation;
        public required string Description;
        public required List<SlotImport> Slots;
        /// <summary>Ordered import targets and their pre-edit states —
        /// captured before anything was written, so a failure restores
        /// exactly what was there and success has a Before half that needs
        /// no re-reading.</summary>
        public required List<TransformTargetId> Order;
        public required Dictionary<TransformTargetId, TransformTargetState> Before;
        /// <summary>Targets the synchronous reset cleared. A reset bone that
        /// had no authored layers did not change and stays out of the
        /// history entry unless a write landed on it.</summary>
        public required HashSet<TransformTargetId> Resets;
        /// <summary>Targets an action (or the model edit) actually wrote.</summary>
        public readonly HashSet<TransformTargetId> Written = new();
        /// <summary>Fires exactly once when an import <see cref="Begin"/>
        /// returned Ok for finishes — with true after the history entry
        /// landed, false after a rollback. A Begin that returned Fail never
        /// fires it; a pending import dropped by Dispose does not either
        /// (session teardown restores animation state wholesale).</summary>
        public Action<bool>? OnFinished;
        public string? Failure;
        public bool Completing;
    }

    private Import? _pending;
    private long _generation;

    public PoseImportCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        IBonePosingService posing,
        ITransformRuntimePort runtime,
        TransformHistory history,
        TransformGestureService gestures,
        IkBakeCapture ikBake,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _posing = posing;
        _runtime = runtime;
        _history = history;
        _gestures = gestures;
        _ikBake = ikBake;
        _log = log;
        _posing.TransitiveActionsEnded += OnTransitiveActionsEnded;
    }

    /// <summary>Whether an import is armed and has not finished: true from
    /// registration until the pass has executed the actions AND the history
    /// entry has been appended (or the whole edit rolled back).</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Arms one plan, on one tick: capture every affected target, apply the
    /// reset scope and the model transform synchronously, register the
    /// per-bone file writes for the next pass. Ok means the import is armed
    /// and its actions are queued — an in-pass failure rolls the whole edit
    /// back and logs, it does not reach this return value.
    /// </summary>
    public GestureResult Begin(
        PoseImportPlan plan,
        string description,
        Action<bool>? onFinished = null)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("Pose import must run on the framework thread.");
        if (_pending != null)
            return GestureResult.Fail("A pose import is already applying.");
        if (_ikBake.IsPending)
            return GestureResult.Fail("An IK bake is still applying.");
        // A live transform gesture owns bones right now; importing under it
        // would interleave two writers on the same targets.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (plan.IsEmpty)
            return GestureResult.Fail("Nothing in this file applies to the chosen scope.");

        var import = new Import
        {
            Generation = ++_generation,
            Description = description,
            Slots = new List<SlotImport>(),
            Order = new List<TransformTargetId>(),
            Before = new Dictionary<TransformTargetId, TransformTargetState>(),
            Resets = new HashSet<TransformTargetId>(),
            OnFinished = onFinished,
        };

        // Resolve and capture EVERYTHING before mutating anything, so a
        // stale target fails synchronously with nothing to roll back.
        TransformPortResult captured;
        var resetBones = new List<(IBone Bone, TransformTargetId Target)>(plan.Resets.Count);
        foreach (var bone in plan.Resets)
        {
            // Virtual bones never carry stacks of their own and have no
            // stable binding — the bake's export predicate skips them too.
            if (bone is VirtualBone)
                continue;
            if (_bindings.GetBoneId(bone) is not { } resetId)
                return GestureResult.Fail(
                    $"Import target {bone.BoneName} could not be resolved.");
            var target = TransformTargetId.ForBone(resetId);
            resetBones.Add((bone, target));
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return GestureResult.Fail(
                        captured.Detail ?? $"Could not capture {target}.");
                import.Before[target] = state;
                import.Order.Add(target);
            }
            import.Resets.Add(target);
        }

        var slotMap = new Dictionary<ISkeleton, SlotImport>();
        foreach (var (bone, file, components) in plan.Writes)
        {
            if (bone is VirtualBone)
                continue;
            if (_bindings.GetBoneId(bone) is not { } writeId)
                return GestureResult.Fail(
                    $"Import target {bone.BoneName} could not be resolved.");
            var target = TransformTargetId.ForBone(writeId);
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return GestureResult.Fail(
                        captured.Detail ?? $"Could not capture {target}.");
                import.Before[target] = state;
                import.Order.Add(target);
            }
            if (!slotMap.TryGetValue(bone.Skeleton, out var slot))
            {
                slotMap[bone.Skeleton] = slot = new SlotImport
                {
                    Skeleton = bone.Skeleton,
                    Writes = new Dictionary<(int, int),
                        (TransformTargetId, Transform, TransformComponents)>(),
                };
                import.Slots.Add(slot);
            }
            slot.Writes[(bone.PartialId, bone.BoneIndex)] = (target, file, components);
        }

        (TransformTargetId Target, PoseTransform Desired)? model = null;
        if (plan.ModelActor is { } modelActor)
        {
            if (_bindings.GetActorId(modelActor) is not { } modelActorId)
                return GestureResult.Fail("The actor could not be resolved.");
            var target = TransformTargetId.ForActor(modelActorId);
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return GestureResult.Fail(
                        captured.Detail ?? $"Could not capture {target}.");
                import.Before[target] = state;
                import.Order.Add(target);
            }
            model = (target, new PoseTransform(
                plan.ModelTransform.Position,
                plan.ModelTransform.Rotation,
                plan.ModelTransform.Scale));
        }

        if (import.Order.Count == 0)
            return GestureResult.Fail("No target of this import could be bound.");

        // Reset-before-import, synchronously — the bake's STEP 2
        // (IkBakeCapture.cs:285-298): clear the scope's authored stacks so
        // the pass diffs against the reset basis. Named service layers stay;
        // their contribution remains in the pass's basis so the file deltas
        // exclude it instead of absorbing a value its owner will re-drive.
        foreach (var (bone, _) in resetBones)
        {
            _posing.GetPoseInfo(bone.Skeleton)
                .GetPoseInfo(bone.BoneName, bone.PartialId)
                .RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());
        }

        // The model transform is an actor edit, outside the skeleton pass —
        // applied synchronously exactly as the replaced ImportEdit did.
        if (model is { } modelEdit)
        {
            var applied = _runtime.ApplyAbsolute(
                import.Before[modelEdit.Target], modelEdit.Desired);
            if (!applied.Success)
            {
                Rollback(import);
                return GestureResult.Fail(
                    applied.Detail ?? "Could not apply the model transform.");
            }
            import.Written.Add(modelEdit.Target);
        }

        // A plan without file writes (reset-only, model-only) has no pass to
        // wait for; it completes here as the same single history entry.
        if (import.Slots.Count == 0)
        {
            var failure = AppendHistory(import);
            if (failure == null)
            {
                Notify(import, true);
                return GestureResult.Ok();
            }
            // Begin itself reports this failure, so the callback stays
            // silent — it fires only for imports Begin returned Ok for.
            _log.Warning($"Pose import failed: {failure}");
            Rollback(import);
            return GestureResult.Fail(failure);
        }

        // Register the per-bone file writes for the next pass — Brio's
        // ImportSkeletonPose (SkeletonPosingCapability.cs:62-66).
        _pending = import;
        foreach (var slot in import.Slots)
        {
            var scope = slot;
            _posing.RegisterTransitiveAction(
                scope.Skeleton,
                (bone, poseInfo) => ApplyBone(import, scope, bone, poseInfo));
        }

        _framework.RunOnTick(
            () => OnTimeout(import.Generation),
            delayTicks: CompletionTimeoutTicks);
        return GestureResult.Ok();
    }

    /// <summary>
    /// Brio's <c>PoseImporter.ApplyBone</c> (Game/Posing/PoseImporter.cs:9-87),
    /// running inside the apply pass. The plan supplies the file absolute and
    /// the component mask; the basis is <c>bone.LastRawTransform</c> exactly
    /// as the pass has just refreshed it; the delta is masked and appended as
    /// a stack the same pass applies immediately.
    ///
    /// Brio's near-identity early-out (PoseInfo.cs:100) is taken on the
    /// MASKED delta, caller-side like the bake's: a bone whose in-scope
    /// components already match its basis must not gain a stack.
    /// </summary>
    private void ApplyBone(
        Import import,
        SlotImport slot,
        IBone bone,
        BonePoseInfo poseInfo)
    {
        try
        {
            if (!slot.Writes.TryGetValue(
                    (bone.PartialId, bone.BoneIndex), out var entry))
                return;

            var desired = entry.File;
            var basis = bone.LastRawTransform;
            var delta = BonePoseInfo.FilterDelta(
                BonePoseInfo.Diff(desired, basis), entry.Components);
            if (IsApproximatelyIdentity(delta))
                return;

            // Propagation stays All (Brio PoseImporter.cs:35, 3rd argument):
            // an imported bone carries its children with it exactly as the
            // pose it replaced did. The mask applies to the delta only.
            if (poseInfo.Apply(
                    desired, basis,
                    Poser.Core.TransformComponents.All,
                    entry.Components) == null)
            {
                import.Failure ??=
                    $"{bone.BoneName} produced a non-finite import delta.";
                return;
            }
            import.Written.Add(entry.Target);
        }
        catch (Exception ex)
        {
            // A throw here is inside the physics detour; swallow it into the
            // import's own failure so the pass stays intact and the whole
            // edit rolls back on completion.
            import.Failure ??= $"{bone.BoneName}: {ex.Message}";
        }
    }

    /// <summary>Raised from the native hooks when the interval that owned a
    /// batch ends. Records only — the completion itself needs the framework
    /// thread.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        if (_pending is not { } import)
            return;
        var complete = true;
        var known = false;
        foreach (var slot in import.Slots)
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
        if (!known || !complete || import.Completing)
            return;
        import.Completing = true;
        _framework.RunOnTick(() => Complete(import.Generation));
    }

    private void OnTimeout(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        import.Failure ??= "The import never reached an apply pass.";
        Complete(generation);
    }

    /// <summary>
    /// The framework-thread half: by now the pass has run the actions and
    /// every imported stack is in place. Capture the after-states of what
    /// actually changed and append ONE history entry — or, on any failure,
    /// restore every captured target and append nothing.
    /// </summary>
    private void Complete(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        _pending = null;

        var failure = import.Failure;
        if (failure == null)
        {
            foreach (var slot in import.Slots)
            {
                if (!slot.Executed)
                {
                    failure = "The apply pass never ran the import.";
                    break;
                }
            }
        }

        if (failure == null)
            failure = AppendHistory(import);

        if (failure != null)
        {
            _log.Warning($"Pose import failed: {failure}");
            Rollback(import);
        }
        Notify(import, failure == null);
    }

    /// <summary>The callback runs application code (the facade's speed
    /// restore); a throw there must not escape into the framework tick or
    /// the pass bookkeeping.</summary>
    private void Notify(Import import, bool success)
    {
        try
        {
            import.OnFinished?.Invoke(success);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose import completion callback threw: {ex.Message}");
        }
    }

    /// <summary>
    /// One undoable entry covering exactly what the import changed: the
    /// bones whose authored stacks the reset cleared, and the targets an
    /// action or the model edit wrote. Unlike the bake, an import that
    /// changed nothing is a legitimate outcome (the file matched the pose)
    /// and appends no entry rather than failing.
    /// </summary>
    private string? AppendHistory(Import import)
    {
        var before = new List<TransformTargetState>();
        var after = new List<TransformTargetState>();
        foreach (var target in import.Order)
        {
            var state = import.Before[target];
            // HasOverride is set by the capture to "this target had authored
            // layers", i.e. exactly what the reset cleared.
            var wasReset = import.Resets.Contains(target) && state.HasOverride;
            if (!wasReset && !import.Written.Contains(target))
                continue;
            var captured = _runtime.Capture(target);
            if (!captured.Success || captured.State is not { } current)
                return captured.Detail ?? $"Could not capture {target}.";
            before.Add(state);
            after.Add(current);
        }

        if (before.Count > 0)
            _history.Append(new TransformPatch(import.Description, before, after));
        return null;
    }

    /// <summary>Puts back every captured state. Nothing of the import
    /// survives a failure.</summary>
    private void Rollback(Import import)
    {
        foreach (var target in import.Order)
        {
            try
            {
                var restored = _runtime.Restore(import.Before[target]);
                if (!restored.Success)
                    _log.Warning(
                        $"Pose import rollback: {restored.Detail ?? target.ToString()}");
            }
            catch (Exception ex)
            {
                _log.Warning($"Pose import rollback threw for {target}: {ex.Message}");
            }
        }
    }

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

    /// <summary>A pending import never outlives the session: its registered
    /// actions die with the posing interval and the completion it was
    /// waiting for is dropped.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
    }
}
