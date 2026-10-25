using Poser.Domain.Transforms;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Operations;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.Application.Posing;

public readonly record struct PoseCaptureResult(
    bool Success,
    PortablePose? Pose,
    string? Detail = null)
{
    public static PoseCaptureResult Ok(PortablePose pose) =>
        new(true, pose);

    public static PoseCaptureResult Fail(string detail) =>
        new(false, null, detail);

    /// <summary>Records any transforms still waiting to be restored after a failed edit.</summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    public bool Equals(PoseCaptureResult other) =>
        Success == other.Success &&
        EqualityComparer<PortablePose?>.Default.Equals(Pose, other.Pose) &&
        Detail == other.Detail;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Pose, Detail);
}

/// <summary>
/// Applies pose edits to a specific actor instance. If the runtime reports a
/// write or final capture failure, it attempts to restore all captured starting
/// values and reports any that could not be restored.
/// </summary>
public sealed class PoseEditService
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private readonly EditHistory _history;
    private readonly TransformGestureService _gestures;

    public PoseEditService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        EditHistory history,
        TransformGestureService gestures)
    {
        _scene = scene;
        _runtime = runtime;
        _history = history;
        _gestures = gestures;
    }

    public PoseEditResult Reset(
        IReadOnlyList<TransformTargetId> targets,
        PoseRegion region,
        string description)
    {
        if (RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return PoseBusy();
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");
        return ResetWithinTransition(targets, region, description, recordHistory: true);
    }

    // Whole-actor Reset and undo call this while holding the edit transition.
    internal PoseEditResult ResetWithinTransition(
        IReadOnlyList<TransformTargetId> targets, PoseRegion region,
        string description, bool recordHistory = false)
    {
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var before = prepared.States!
            .Where(state =>
                state.Target.Bone is { } bone &&
                // Body, Face, and Hair reset bones in the character skeleton.
                (region == PoseRegion.All ||
                 bone.Slot == PoseSlot.Character) &&
                MatchesRegion(bone.CanonicalName, region) &&
                state.Pose.Layers.Count > 0)
            .ToArray();
        var desired = before
            .Select(state => state with
            {
                Pose = PoseOperations.Reset(state.Pose),
                HasOverride = false,
            })
            .ToArray();
        return Apply(description, before, desired, recordHistory);
    }

    /// <summary>
    /// Mirrors the selected bone's pose edits across the body's left-right plane.
    /// </summary>
    public PoseEditResult Flip(
        TransformTargetId target,
        string description)
    {
        if (RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return PoseBusy();
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");
        var prepared = CaptureBones(new[] { target });
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var before = prepared.States!;
        if (before[0].Pose.Layers.Count == 0)
            return PoseEditResult.Fail(
                "No Poser-authored adjustment on this bone to flip.");
        var desired = before.Select(state => state with
        {
            Pose = PoseOperations.MirrorRebased(
                state.Pose,
                state.AnimatedBaselineRotation,
                state.AnimatedBaselineRotation),
            HasOverride = state.Pose.Layers.Count > 0,
        }).ToArray();
        return Apply(description, before, desired);
    }

    /// <summary>
    /// Mirrors pose edits as one undo step. Left and right bones exchange edits;
    /// center and unpaired bones mirror in place. The actor's rotation is mirrored
    /// around its current position. Unedited bones keep following animation.
    /// </summary>
    public PoseEditResult Mirror(
        IReadOnlyList<TransformTargetId> targets,
        string description)
    {
        if (RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return PoseBusy();
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");

        var boneTargets = targets
            .Where(target => target.Kind == TransformTargetKind.Bone)
            .ToArray();
        var actorTargets = targets
            .Where(target => target.Kind == TransformTargetKind.Actor)
            .Distinct()
            .ToArray();
        if (targets.Count != boneTargets.Length + actorTargets.Length)
            return PoseEditResult.Fail(
                "Mirror accepts bone and actor targets only.");
        var prepared = CaptureBones(boneTargets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var before = prepared.States!;
        var byKey = before
            .Where(state => state.Target.Bone != null)
            .ToDictionary(
                state => Key(state.Target.Bone!.Value),
                state => state);
        var desired = new List<TransformTargetState>();
        var processed = new HashSet<TransformTargetId>();

        foreach (var state in before)
        {
            if (state.Target.Bone is not { } bone ||
                !processed.Add(state.Target))
                continue;

            TransformTargetState? partner = null;
            if (MirrorSlot(bone.Slot) is { } partnerSlot)
            {
                // Match weapon bones by name and partial in the opposite hand.
                if (byKey.TryGetValue(
                        (partnerSlot, bone.PartialId, bone.CanonicalName),
                        out var handed))
                    partner = handed;
            }
            else if (MirrorName(bone.CanonicalName) is { } partnerName &&
                     byKey.TryGetValue(
                         (bone.Slot, bone.PartialId, partnerName),
                         out var candidate))
                partner = candidate;

            if (partner is { } counterpart)
            {
                processed.Add(counterpart.Target);
                if (state.Pose.Layers.Count == 0 &&
                    counterpart.Pose.Layers.Count == 0)
                    continue;
                desired.Add(state with
                {
                    Pose = PoseOperations.MirrorRebased(
                        counterpart.Pose,
                        counterpart.AnimatedBaselineRotation,
                        state.AnimatedBaselineRotation),
                    HasOverride = counterpart.Pose.Layers.Count > 0,
                });
                desired.Add(counterpart with
                {
                    Pose = PoseOperations.MirrorRebased(
                        state.Pose,
                        state.AnimatedBaselineRotation,
                        counterpart.AnimatedBaselineRotation),
                    HasOverride = state.Pose.Layers.Count > 0,
                });
            }
            else if (state.Pose.Layers.Count > 0)
            {
                // Mirror center and unpaired bones in place.
                desired.Add(state with
                {
                    Pose = PoseOperations.MirrorRebased(
                        state.Pose,
                        state.AnimatedBaselineRotation,
                        state.AnimatedBaselineRotation),
                    HasOverride = true,
                });
            }
        }

        var changedTargets = desired
            .Select(state => state.Target)
            .ToHashSet();
        var changedBefore = before
            .Where(state => changedTargets.Contains(state.Target))
            .ToList();

        var lineage = boneTargets[0].ActorLineage;
        foreach (var target in actorTargets)
        {
            if (target.ActorLineage != lineage)
                return PoseEditResult.Fail(
                    "A pose edit cannot span actor lineages.");
            if (!_scene.Contains(target))
                return PoseEditResult.Fail("A pose target is stale.");
            var captured = _runtime.Capture(target);
            if (!captured.Success || captured.State == null)
                return PoseEditResult.Fail(
                    captured.Detail ?? $"Could not capture {target}.");
            var state = captured.State;
            // Preserve the game's placement until the actor has been moved in Poser.
            if (!state.HasOverride)
                continue;
            changedBefore.Add(state);
            desired.Add(state with
            {
                Transform = state.Transform with
                {
                    Rotation = Domain.Transforms.TransformMath
                        .NormalizeRotation(
                            PoseOperations.MirrorRotation(
                                state.Transform.Rotation)),
                },
            });
        }

        return Apply(description, changedBefore, desired);
    }

    /// <summary>Pairs the two weapon hands for mirroring.</summary>
    private static PoseSlot? MirrorSlot(PoseSlot slot) => slot switch
    {
        PoseSlot.MainHand => PoseSlot.OffHand,
        PoseSlot.OffHand => PoseSlot.MainHand,
        _ => null,
    };

    private static string? MirrorName(string canonicalName)
    {
        if (canonicalName.EndsWith("_l", StringComparison.Ordinal))
            return string.Concat(
                canonicalName.AsSpan(0, canonicalName.Length - 2), "_r");
        if (canonicalName.EndsWith("_r", StringComparison.Ordinal))
            return string.Concat(
                canonicalName.AsSpan(0, canonicalName.Length - 2), "_l");
        return null;
    }

    public PoseCaptureResult CapturePortable(
        IReadOnlyList<TransformTargetId> targets)
    {
        if (_gestures.PendingRecovery is { } recovery)
            return PoseCaptureResult.Fail(
                "Transform recovery must complete before another operation.") with
            {
                Recovery = recovery,
            };
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return PoseCaptureResult.Fail(
                "A transform application transition is busy.") with
            {
                Recovery = _gestures.PendingRecovery,
            };
        if (_gestures.ActiveGesture != null)
            return PoseCaptureResult.Fail(
                "A transform gesture is active.");
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseCaptureResult.Fail(prepared.Detail!);

        var bones = SceneBonesById();
        var entries = new List<PortableBoneEntry>(prepared.States!.Count);
        foreach (var state in prepared.States)
        {
            if (!TryCreatePortableTarget(
                    state.Target.Bone!.Value,
                    bones,
                    out var portableTarget,
                    out var detail))
                return PoseCaptureResult.Fail(detail!);
            entries.Add(new PortableBoneEntry(
                portableTarget.Key,
                state.Pose.InteractiveOnly(),
                portableTarget.NativeIndexHint));
        }

        try
        {
            return PoseCaptureResult.Ok(new PortablePose(entries));
        }
        catch (ArgumentException exception)
        {
            return PoseCaptureResult.Fail(
                $"Captured portable pose is not structurally representable: {exception.Message}");
        }
    }

    public PoseEditResult ApplyPortable(
        IReadOnlyList<TransformTargetId> targets,
        PortablePose pose,
        string description)
    {
        if (RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return PoseBusy();
        ArgumentNullException.ThrowIfNull(pose);
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");

        if (!TryValidateBoneTargets(targets, out var targetDetail))
            return PoseEditResult.Fail(targetDetail!);

        var bones = SceneBonesById();
        var destinations = new List<PortableBoneTarget>(targets.Count);
        foreach (var target in targets.Distinct())
        {
            if (!TryCreatePortableTarget(
                    target.Bone!.Value,
                    bones,
                    out var destination,
                    out var detail))
                return PoseEditResult.Fail(detail!);
            destinations.Add(destination);
        }

        // Refuse an entry that matches more than one destination because its
        // target is ambiguous. Missing bones are skipped and reported, allowing
        // poses from a richer skeleton to apply to the bones both actors share.
        var match = pose.Match(destinations);
        if (match.Ambiguous.Count > 0)
            return PoseEditResult.Fail(DescribeMatchFailure(match)!);

        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var statesByBone = prepared.States!.ToDictionary(
            state => state.Target.Bone!.Value);
        var before = match.Matches
            .Select(item => statesByBone[item.Target.Bone])
            .ToArray();
        if (before.Length == 0)
            return PoseEditResult.Fail(
                DescribeMatchFailure(match) ??
                "The portable pose has no bones matching this skeleton.");

        var desired = match.Matches.Select(item =>
        {
            var state = statesByBone[item.Target.Bone];
            var source = item.Source.Pose;
            var transferred = new BonePose(
                source.Layers,
                checked(state.Pose.Version + 1));
            return state with
            {
                Pose = transferred,
                HasOverride = transferred.Layers.Count > 0,
            };
            }).ToArray();
        var applied = Apply(description, before, desired);
        return applied.Success && match.Unmatched.Count > 0
            ? applied with { Detail = DescribeSkipped(match.Unmatched) }
            : applied;
    }

    /// <summary>Index of scene bones by ID, built once per capture or apply pass
    /// to avoid scanning all bones again for each entry.</summary>
    private Dictionary<BoneId, BoneDescriptor> SceneBonesById() =>
        _scene.Snapshot.Actors
            .SelectMany(actor => actor.Skeletons)
            .SelectMany(skeleton => skeleton.Bones)
            .ToDictionary(item => item.Id);

    private static bool TryCreatePortableTarget(
        BoneId bone,
        IReadOnlyDictionary<BoneId, BoneDescriptor> bones,
        out PortableBoneTarget target,
        out string? detail)
    {
        if (!bones.TryGetValue(bone, out var descriptor))
        {
            target = default;
            detail = $"Could not construct a structural path for bone {bone}.";
            return false;
        }

        var segments = new List<string>();
        var seen = new HashSet<BoneId>();
        var current = descriptor;
        while (true)
        {
            if (!seen.Add(current.Id) ||
                string.IsNullOrWhiteSpace(current.Id.CanonicalName))
            {
                target = default;
                detail = $"Bone parent graph is invalid for {bone}.";
                return false;
            }

            segments.Add(current.Id.CanonicalName);
            if (current.Parent is not { } parent)
                break;
            if (!bones.TryGetValue(parent, out var parentDescriptor))
            {
                target = default;
                detail = $"Bone parent {parent} is missing for {bone}.";
                return false;
            }
            current = parentDescriptor;
        }

        segments.Reverse();
        target = PortableBoneTarget.From(
            bone,
            new BonePath(segments),
            bone.BoneIndex);
        detail = null;
        return true;
    }

    private static string? DescribeMatchFailure(PortablePoseMatchResult match)
    {
        var details = new List<string>();
        if (match.Ambiguous.Count > 0)
            details.Add(
                $"Ambiguous portable bones: {NameList(match.Ambiguous)}.");
        if (match.Unmatched.Count > 0)
            details.Add(
                $"Unmatched portable bones: {NameList(match.Unmatched)}.");
        return details.Count == 0 ? null : string.Join(" ", details);
    }

    /// <summary>Reports bones skipped from an otherwise successful paste.</summary>
    private static string DescribeSkipped(
        IReadOnlyList<PortableBoneMatchFailure> unmatched) =>
        $"Skipped {unmatched.Count} bone(s) this skeleton does not have: {NameList(unmatched)}.";

    /// <summary>At most eight bone names, followed by the remaining count,
    /// to keep failure notifications readable.</summary>
    private static string NameList(
        IReadOnlyList<PortableBoneMatchFailure> failures)
    {
        const int Shown = 8;
        var names = failures
            .Take(Shown)
            .Select(item => item.Entry.Key.CanonicalName);
        return failures.Count <= Shown
            ? string.Join(", ", names)
            : $"{string.Join(", ", names)} and {failures.Count - Shown} more";
    }

    private CaptureResult CaptureBones(
        IReadOnlyList<TransformTargetId> targets)
    {
        if (!TryValidateBoneTargets(targets, out var detail))
            return CaptureResult.Fail(detail!);

        var states = new List<TransformTargetState>(targets.Count);
        foreach (var target in targets.Distinct())
        {
            var captured = _runtime.Capture(target);
            if (!captured.Success || captured.State == null)
                return CaptureResult.Fail(
                    captured.Detail ?? $"Could not capture {target}.");
            states.Add(captured.State);
        }
        return CaptureResult.Ok(states);
    }

    private bool TryValidateBoneTargets(
        IReadOnlyList<TransformTargetId> targets,
        out string? detail)
    {
        if (targets.Count == 0)
        {
            detail = "A pose edit requires at least one bone.";
            return false;
        }
        if (targets.Any(target => target.Kind != TransformTargetKind.Bone))
        {
            detail = "Pose edits accept bone targets only.";
            return false;
        }
        if (targets.Any(target => !_scene.Contains(target)))
        {
            detail = "A pose target is stale.";
            return false;
        }
        var lineage = targets[0].ActorLineage;
        if (targets.Any(target => target.ActorLineage != lineage))
        {
            detail = "A pose edit cannot span actor lineages.";
            return false;
        }

        detail = null;
        return true;
    }

    private PoseEditResult Apply(
        string description,
        IReadOnlyList<TransformTargetState> before,
        IReadOnlyList<TransformTargetState> desired,
        bool recordHistory = true)
    {
        if (desired.Count == 0)
            return PoseEditResult.Ok(0);

        foreach (var state in desired)
        {
            var result = _runtime.Restore(state);
            if (result.Success)
                continue;
            var rollback = _gestures.AttemptRecovery(before);
            return FailureAfterRecovery(
                result.Detail ?? $"Could not apply pose to {state.Target}.",
                rollback);
        }

        var after = new List<TransformTargetState>(desired.Count);
        foreach (var state in desired)
        {
            var result = _runtime.Capture(state.Target);
            if (!result.Success || result.State == null)
            {
                var rollback = _gestures.AttemptRecovery(before);
                return FailureAfterRecovery(
                    result.Detail ??
                    $"Could not capture final pose for {state.Target}.",
                    rollback);
            }
            after.Add(result.State);
        }

        if (recordHistory)
            _history.Append(new TransformPatch(description, before, after));
        return PoseEditResult.Ok(desired.Count);
    }

    /// <summary>
    /// Retries pending transform recovery before a pose workflow proceeds.
    /// Returns a refusal if recovery remains incomplete.
    /// </summary>
    internal PoseEditResult? RecoveryBarrier() =>
        !_gestures.TryCompleteRecovery() &&
        _gestures.PendingRecovery is { } recovery
            ? PoseEditResult.Fail(
                "Transform recovery must complete before another mutation.") with
            {
                Recovery = recovery,
            }
            : null;

    private PoseEditResult PoseBusy() =>
        PoseEditResult.Fail(
            "A transform application transition is busy.") with
        {
            Recovery = _gestures.PendingRecovery,
        };

    private static PoseEditResult FailureAfterRecovery(
        string primaryFailure,
        TransformRecoveryReceipt recovery) =>
        PoseEditResult.Fail(TransformRecovery.AppendRollbackFailure(
            primaryFailure,
            recovery)) with
        {
            Recovery = recovery,
        };

    private static (PoseSlot Slot, int Partial, string Name) Key(
        BoneId bone) =>
        (bone.Slot, bone.PartialId, bone.CanonicalName);

    private static bool MatchesRegion(
        string name,
        PoseRegion region)
    {
        var face =
            name.StartsWith("j_f_", StringComparison.Ordinal) ||
            name.Equals("j_kao", StringComparison.Ordinal) ||
            name.StartsWith("j_ago", StringComparison.Ordinal);
        var hair =
            name.StartsWith("j_kami", StringComparison.Ordinal) ||
            name.StartsWith("j_ex_h", StringComparison.Ordinal) ||
            name.StartsWith("j_ex_met", StringComparison.Ordinal);
        return region switch
        {
            PoseRegion.All => true,
            PoseRegion.Face => face,
            PoseRegion.Hair => hair,
            PoseRegion.Body => !face && !hair,
            _ => false,
        };
    }

    private sealed record CaptureResult(
        bool Success,
        IReadOnlyList<TransformTargetState>? States,
        string? Detail)
    {
        public static CaptureResult Ok(
            IReadOnlyList<TransformTargetState> states) =>
            new(true, states, null);

        public static CaptureResult Fail(string detail) =>
            new(false, null, detail);
    }
}
