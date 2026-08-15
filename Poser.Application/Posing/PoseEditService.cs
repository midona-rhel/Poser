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
    ///
    /// <para>Weapons exchange HANDS. A main-hand bone's counterpart is the
    /// same bone in the off hand rather than a <c>_l</c>/<c>_r</c> sibling
    /// inside its own skeleton, because a weapon skeleton has no lateral
    /// pairs — Brio moves the two dictionaries across wholesale for the same
    /// reason (PosingCapability.cs:538-547). Prop and ornament keep pairing
    /// within themselves; Brio leaves both as an explicit TODO and there is
    /// no second slot to exchange them with.</para>
    ///
    /// <para>An ACTOR target mirrors its authored model ROTATION in place.
    /// Brio also reflects the model POSITION's X (MirrorModelTransform), which
    /// reflects the actor across the world origin — a meaningless point on any
    /// real map, and the one part of its mirror Brio annotates as not working
    /// right. Mirroring which way the actor faces is the half that pairs with
    /// mirroring its body; where it stands is not a left/right fact.</para>
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
                // A hand's counterpart is the SAME bone in the other hand. A
                // main-hand bone with no off hand present falls through to the
                // in-place branch below rather than vanishing, which is where
                // Brio's wholesale dictionary move loses it.
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
            // Authored edits only, the same contract the bones keep: an actor
            // the user has never moved keeps whatever the game placed it at.
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

    /// <summary>The slot a bone's mirror counterpart lives in when it is not
    /// this bone's own — the two weapon hands, and nothing else.</summary>
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

        // AMBIGUITY still refuses wholesale: an entry matching several
        // destinations has no safe answer, so writing any of them would be a
        // guess. An UNMATCHED entry has an answer — this skeleton simply does
        // not have that bone — so the intersection is applied and the skipped
        // names are reported, which is what both references do (Brio
        // PoseImporter.cs:33-37, Ktisis PoseContainer.cs:179). Refusing the
        // whole paste made a pose captured on a richer skeleton (IVCS, tail,
        // extra partials) unusable on a poorer one.
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

    /// <summary>Every bone of every actor in the scene, by id. Built ONCE
    /// per capture/apply pass and passed through the per-bone loop —
    /// rebuilding it per bone made portable capture O(bones²) on the
    /// framework thread.</summary>
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

    /// <summary>The success-side half of the refusal vocabulary: the paste
    /// landed, and this names what it could not carry.</summary>
    private static string DescribeSkipped(
        IReadOnlyList<PortableBoneMatchFailure> unmatched) =>
        $"Skipped {unmatched.Count} bone(s) this skeleton does not have: {NameList(unmatched)}.";

    /// <summary>Bone names, TRUNCATED — a paste from a much richer skeleton
    /// can miss hundreds of bones and this text reaches a notification.</summary>
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
