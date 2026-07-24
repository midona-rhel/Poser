using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

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
}

/// <summary>Atomic stable-id commands for discrete manual pose edits.</summary>
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
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var before = prepared.States!
            .Where(state =>
                state.Target.Bone is { } bone &&
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

    /// <summary>
    /// "Bake mirrored pose" (correction 3D): mirrors the actor's currently
    /// evaluated body pose — including animation-derived transforms — and
    /// materializes it as authored pose state. Follows Ktisis
    /// EntityPoseConverter.FlipPose: opposite-name rotation exchange in
    /// model space (mirror quaternion (−x, −y, z, w)), positions untouched,
    /// face/hair/j_ex partials and iv_/ya_ bones excluded, root yaw
    /// corrected and flipped 180° about Y. All desired results derive from
    /// one immutable snapshot; the operation is one atomic history entry
    /// with full rollback on failure.
    /// </summary>
    public PoseEditResult BakeMirroredPose(
        IReadOnlyList<TransformTargetId> targets,
        string description)
    {
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        static bool Eligible(BoneId bone) =>
            bone.PartialId is not (1 or 2 or 4) &&
            !bone.CanonicalName.StartsWith("iv_", StringComparison.Ordinal) &&
            !bone.CanonicalName.StartsWith("ya_", StringComparison.Ordinal);

        var before = prepared.States!
            .Where(state => state.Target.Bone is { } bone && Eligible(bone))
            .ToArray();
        if (before.Length == 0)
            return PoseEditResult.Fail("No body bones available to bake.");

        var byKey = before.ToDictionary(
            state => Key(state.Target.Bone!.Value),
            state => state);
        var rootId = FindSkeletonRoot(before[0].Target.Bone!.Value);

        var desired = new List<(TransformTargetState State, Domain.Transforms.PoseTransform Transform)>(before.Length);
        foreach (var state in before)
        {
            var bone = state.Target.Bone!.Value;
            var source = state;
            if (MirrorName(bone.CanonicalName) is { } partnerName &&
                byKey.TryGetValue(
                    (bone.Slot, bone.PartialId, partnerName),
                    out var counterpart))
                source = counterpart;

            var rotation = PoseOperations.MirrorRotation(source.Transform.Rotation);
            if (rootId is { } root && bone.Equals(root))
                rotation = CorrectRootRotation(state.Transform.Rotation, rotation);

            desired.Add((state, state.Transform with { Rotation = Quaternion.Normalize(rotation) }));
        }

        var appliedCount = 0;
        foreach (var (state, transform) in desired)
        {
            var result = _runtime.ApplyAbsolute(state, transform);
            if (!result.Success)
            {
                RestoreAll(before);
                return PoseEditResult.Fail(
                    result.Detail ?? $"Could not bake {state.Target}.");
            }
            appliedCount++;
        }

        var after = new List<TransformTargetState>(before.Length);
        foreach (var state in before)
        {
            var result = _runtime.Capture(state.Target);
            if (!result.Success || result.State == null)
            {
                RestoreAll(before);
                return PoseEditResult.Fail(
                    result.Detail ??
                    $"Could not capture baked pose for {state.Target}.");
            }
            after.Add(result.State);
        }

        _history.Append(new TransformPatch(description, before, after));
        return PoseEditResult.Ok(appliedCount);
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

    private BoneId? FindSkeletonRoot(BoneId sample)
    {
        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != sample.Skeleton.Actor.LogicalId ||
                actor.Skeleton is not { } skeleton)
                continue;
            foreach (var bone in skeleton.Bones)
                if (bone.Parent == null && bone.Id.PartialId == 0)
                    return bone.Id;
            return null;
        }
        return null;
    }

    /// <summary>Ktisis FlipPose root correction: re-aim the flipped root's
    /// yaw at the pre-flip forward direction, then flip 180° about Y.</summary>
    private static Quaternion CorrectRootRotation(
        Quaternion initial,
        Quaternion flipped)
    {
        static float YawOf(Quaternion rotation)
        {
            var forward = Vector3.Transform(Vector3.UnitZ, rotation);
            return MathF.Atan2(forward.X, forward.Z);
        }

        var corrected = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(
                Vector3.UnitY, YawOf(initial) - YawOf(flipped)) * flipped);
        return Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) * corrected);
    }

    public PoseCaptureResult CapturePortable(
        IReadOnlyList<TransformTargetId> targets)
    {
        if (_gestures.ActiveGesture != null)
            return PoseCaptureResult.Fail(
                "A transform gesture is active.");
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseCaptureResult.Fail(prepared.Detail!);

        var pose = new PortablePose(
            prepared.States!.Select(state =>
                new PortableBonePose(
                    PortableBoneId.From(state.Target.Bone!.Value),
                    state.Pose.InteractiveOnly())));
        return PoseCaptureResult.Ok(pose);
    }

    public PoseEditResult ApplyPortable(
        IReadOnlyList<TransformTargetId> targets,
        PortablePose pose,
        string description)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (_gestures.ActiveGesture != null)
            return PoseEditResult.Fail(
                "A transform gesture is active.");
        var prepared = CaptureBones(targets);
        if (!prepared.Success)
            return PoseEditResult.Fail(prepared.Detail!);

        var before = prepared.States!
            .Where(state =>
                pose.TryGet(
                    PortableBoneId.From(state.Target.Bone!.Value),
                    out _))
            .ToArray();
        if (before.Length == 0)
            return PoseEditResult.Fail(
                "The portable pose has no bones matching this skeleton.");

        var desired = before.Select(state =>
        {
            pose.TryGet(
                PortableBoneId.From(state.Target.Bone!.Value),
                out var source);
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

    private CaptureResult CaptureBones(
        IReadOnlyList<TransformTargetId> targets)
    {
        if (targets.Count == 0)
            return CaptureResult.Fail("A pose edit requires at least one bone.");
        if (targets.Any(target => target.Kind != TransformTargetKind.Bone))
            return CaptureResult.Fail("Pose edits accept bone targets only.");
        if (targets.Any(target => !_scene.Contains(target)))
            return CaptureResult.Fail("A pose target is stale.");
        var lineage = targets[0].ActorLineage;
        if (targets.Any(target => target.ActorLineage != lineage))
            return CaptureResult.Fail(
                "A pose edit cannot span actor lineages.");

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
            RestoreAll(before);
            return PoseEditResult.Fail(
                result.Detail ?? $"Could not apply pose to {state.Target}.");
        }

        var after = new List<TransformTargetState>(desired.Count);
        foreach (var state in desired)
        {
            var result = _runtime.Capture(state.Target);
            if (!result.Success || result.State == null)
            {
                RestoreAll(before);
                return PoseEditResult.Fail(
                    result.Detail ??
                    $"Could not capture final pose for {state.Target}.");
            }
            after.Add(result.State);
        }

        _history.Append(new TransformPatch(description, before, after));
        return PoseEditResult.Ok(desired.Count);
    }

    private void RestoreAll(
        IReadOnlyList<TransformTargetState> states)
    {
        foreach (var state in states)
            _runtime.Restore(state);
    }

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
