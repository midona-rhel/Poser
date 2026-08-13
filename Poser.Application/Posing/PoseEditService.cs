using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Operations;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.Application.Posing;

public enum PoseRegion
{
    All,
    Body,
    Face,
    Hair,
}

public readonly record struct PoseEditResult(
    bool Success,
    int Affected,
    string? Detail = null)
{
    public static PoseEditResult Ok(int affected) =>
        new(true, affected);

    public static PoseEditResult Fail(string detail) =>
        new(false, 0, detail);

    /// <summary>Additive evidence, excluded from legacy positional equality.</summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    /// <summary>Additive operation evidence, excluded from legacy positional
    /// equality, hashing, and deconstruction.</summary>
    public OperationReceipt? OperationReceipt { get; init; }

    public bool Equals(PoseEditResult other) =>
        Success == other.Success &&
        Affected == other.Affected &&
        Detail == other.Detail;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Affected, Detail);
}

public readonly record struct PoseCaptureResult(
    bool Success,
    PortablePose? Pose,
    string? Detail = null)
{
    public static PoseCaptureResult Ok(PortablePose pose) =>
        new(true, pose);

    public static PoseCaptureResult Fail(string detail) =>
        new(false, null, detail);

    /// <summary>Additive evidence, excluded from legacy positional equality.</summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    public bool Equals(PoseCaptureResult other) =>
        Success == other.Success &&
        EqualityComparer<PortablePose?>.Default.Equals(Pose, other.Pose) &&
        Detail == other.Detail;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Pose, Detail);
}

/// <summary>
/// Stable-id pose edits with exhaustive rollback and typed recovery evidence.
/// </summary>
public sealed class PoseEditService
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private readonly TransformHistory _history;
    private readonly TransformGestureService _gestures;

    public PoseEditService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        TransformHistory history,
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
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var before = prepared.States!
            .Where(state =>
                state.Target.Bone is { } bone &&
                // Body/Face/Hair regions are Character-only by contract; a
                // region reset can never touch a same-named auxiliary bone.
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
        return Apply(description, before, desired);
    }

    /// <summary>
    /// Bone-level authored-delta reflection (correction 3C): reflects only
    /// the selected bone's Poser-authored adjustment through the sagittal
    /// plane, rebased in its own frozen animated baseline. It never bakes
    /// animation, and an untouched bone reports a clear no-edit result.
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
    /// "Mirror edits" (correction 3A): the animation-safe actor operation.
    /// Only Poser-authored layers move — left/right counterparts exchange
    /// their authored adjustments (converted counterpart-frame-aware, 3B),
    /// center/unpaired bones with authored adjustments mirror in place, and
    /// bones without authored adjustments stay driven by their animation.
    /// The current evaluated animation is never captured or baked; one
    /// atomic history entry covers the whole actor.
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
        var prepared = CaptureBones(targets);
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
            if (MirrorName(bone.CanonicalName) is { } partnerName &&
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
                // Center or unpaired bone: mirror its own authored
                // adjustment in place.
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
            .ToArray();
        return Apply(description, changedBefore, desired);
    }

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

        var entries = new List<PortableBoneEntry>(prepared.States!.Count);
        foreach (var state in prepared.States)
        {
            if (!TryCreatePortableTarget(
                    state.Target.Bone!.Value,
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

        var destinations = new List<PortableBoneTarget>(targets.Count);
        foreach (var target in targets.Distinct())
        {
            if (!TryCreatePortableTarget(
                    target.Bone!.Value,
                    out var destination,
                    out var detail))
                return PoseEditResult.Fail(detail!);
            destinations.Add(destination);
        }

        var match = pose.Match(destinations);
        if (match.Ambiguous.Count > 0 || match.Unmatched.Count > 0)
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
        return Apply(description, before, desired);
    }

    private bool TryCreatePortableTarget(
        BoneId bone,
        out PortableBoneTarget target,
        out string? detail)
    {
        if (!TryFindBoneDescriptor(bone, out var descriptor))
        {
            target = default;
            detail = $"Could not construct a structural path for bone {bone}.";
            return false;
        }

        var parents = _scene.Snapshot.Actors
            .SelectMany(actor => actor.Skeletons)
            .SelectMany(skeleton => skeleton.Bones)
            .ToDictionary(item => item.Id);
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
            if (!parents.TryGetValue(parent, out var parentDescriptor))
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

    private bool TryFindBoneDescriptor(
        BoneId bone,
        out BoneDescriptor descriptor)
    {
        foreach (var actor in _scene.Snapshot.Actors)
        foreach (var skeleton in actor.Skeletons)
        foreach (var candidate in skeleton.Bones)
        {
            if (candidate.Id == bone)
            {
                descriptor = candidate;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }

    private static string? DescribeMatchFailure(PortablePoseMatchResult match)
    {
        var details = new List<string>();
        if (match.Ambiguous.Count > 0)
            details.Add(
                $"Ambiguous portable bones: {string.Join(", ", match.Ambiguous.Select(item => item.Entry.Key.CanonicalName))}.");
        if (match.Unmatched.Count > 0)
            details.Add(
                $"Unmatched portable bones: {string.Join(", ", match.Unmatched.Select(item => item.Entry.Key.CanonicalName))}.");
        return details.Count == 0 ? null : string.Join(" ", details);
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
        IReadOnlyList<TransformTargetState> desired)
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

        _history.Append(new TransformPatch(description, before, after));
        return PoseEditResult.Ok(desired.Count);
    }

    /// <summary>
    /// Read-only barrier projection for Application-owned pose workflows that
    /// must reject before inspecting their own local state.
    /// </summary>
    internal PoseEditResult? RecoveryBarrier() =>
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
