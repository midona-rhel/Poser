using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

/// <summary>
/// Reset, Mirror and Flip. Each command works out the new pose edits for the
/// bones it changes. Reading the bones, writing them, recording the undo step
/// and putting things back after a failed write are done by
/// <see cref="TransformCommands"/>, exactly as for a typed transform edit.
/// </summary>
public sealed class PoseEditService(
    ITransformRuntimePort runtime,
    TransformCommands transforms)
{
    public PoseEditResult Reset(
        IReadOnlyList<TransformTargetId> bones,
        PoseRegion region,
        string description)
    {
        if (Refusal() is { } refused)
            return refused;
        if (Read(bones, out var states) is { } failed)
            return failed;
        var before = Edited(states, region);
        return Apply(before, before.Select(Cleared).ToArray(), description);
    }

    /// <summary>
    /// Clears every bone's edits without recording an undo step. Reset all calls
    /// this. It first checks that no earlier failed edit is still being undone,
    /// and records one undo step for the pose and everything else it resets.
    /// </summary>
    internal PoseEditResult ResetBones(IReadOnlyList<TransformTargetId> bones)
    {
        if (Read(bones, out var states) is { } failed)
            return failed;
        var before = Edited(states, PoseRegion.All);
        if (before.Length == 0)
            return PoseEditResult.Ok(0);
        var desired = before.Select(Cleared).ToArray();
        return Result(
            transforms.Write(before, (_, index) => runtime.Restore(desired[index]), parentsFirst: false, out _),
            desired.Length);
    }

    /// <summary>Mirrors one bone's own edits across the body's left-right plane.</summary>
    public PoseEditResult Flip(
        TransformTargetId bone,
        string description)
    {
        if (Refusal() is { } refused)
            return refused;
        if (Read([bone], out var states) is { } failed)
            return failed;
        var state = states[0];
        if (state.Pose.Layers.Count == 0)
            return PoseEditResult.Fail(
                "No Poser-authored adjustment on this bone to flip.");
        var flipped = state with
        {
            Pose = PoseOperations.MirrorRebased(
                state.Pose,
                state.AnimatedBaselineRotation,
                state.AnimatedBaselineRotation),
        };
        return Apply([state], [flipped], description);
    }

    /// <summary>
    /// Mirrors an actor's pose edits as one undo step. Left and right bones
    /// exchange edits; center and unpaired bones mirror in place. The actor's
    /// facing is mirrored around its current position. Unedited bones keep
    /// following the animation.
    /// </summary>
    public PoseEditResult Mirror(
        IReadOnlyList<TransformTargetId> bones,
        TransformTargetId actor,
        string description)
    {
        if (Refusal() is { } refused)
            return refused;
        if (Read([.. bones, actor], out var states) is { } failed)
            return failed;

        var boneStates = states.Take(bones.Count).ToArray();
        var byKey = boneStates.ToDictionary(state => Key(state.Target.Bone!.Value));
        var before = new List<TransformTargetState>();
        var desired = new List<TransformTargetState>();
        var processed = new HashSet<TransformTargetId>();

        foreach (var state in boneStates)
        {
            if (!processed.Add(state.Target))
                continue;
            var bone = state.Target.Bone!.Value;

            TransformTargetState? partner = null;
            // Weapon bones pair with the bone of the same name in the other hand;
            // body bones pair by their _l and _r name endings.
            if (MirrorSlot(bone.Slot) is { } partnerSlot)
            {
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
                before.Add(state);
                desired.Add(state with
                {
                    Pose = PoseOperations.MirrorRebased(
                        counterpart.Pose,
                        counterpart.AnimatedBaselineRotation,
                        state.AnimatedBaselineRotation),
                });
                before.Add(counterpart);
                desired.Add(counterpart with
                {
                    Pose = PoseOperations.MirrorRebased(
                        state.Pose,
                        state.AnimatedBaselineRotation,
                        counterpart.AnimatedBaselineRotation),
                });
            }
            else if (state.Pose.Layers.Count > 0)
            {
                before.Add(state);
                desired.Add(state with
                {
                    Pose = PoseOperations.MirrorRebased(
                        state.Pose,
                        state.AnimatedBaselineRotation,
                        state.AnimatedBaselineRotation),
                });
            }
        }

        // An actor the game still places itself is left where the game puts it.
        var placement = states[^1];
        if (placement.HasOverride)
        {
            before.Add(placement);
            desired.Add(placement with
            {
                Transform = placement.Transform with
                {
                    Rotation = TransformMath.NormalizeRotation(
                        PoseOperations.MirrorRotation(placement.Transform.Rotation)),
                },
            });
        }

        return Apply(before, desired, description);
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

    private PoseEditResult? Refusal() =>
        transforms.Refusal() is { } refused ? PoseEditResult.Fail(refused.Detail!) : null;

    private PoseEditResult? Read(
        IReadOnlyList<TransformTargetId> targets,
        out List<TransformTargetState> states)
    {
        var read = transforms.Read(targets, out states);
        return read.Success ? null : PoseEditResult.Fail(read.Detail!);
    }

    /// <summary>
    /// Writes each prepared state as one undo step. <paramref name="before"/>
    /// and <paramref name="desired"/> list the same bones in the same order.
    /// A command that finds nothing to change, such as Reset on bones with no
    /// edits, records no step.
    /// </summary>
    private PoseEditResult Apply(
        IReadOnlyList<TransformTargetState> before,
        IReadOnlyList<TransformTargetState> desired,
        string description) =>
        desired.Count == 0
            ? PoseEditResult.Ok(0)
            : Result(
                transforms.Commit(before, (_, index) => runtime.Restore(desired[index]), description),
                desired.Count);

    private static PoseEditResult Result(GestureResult result, int affected) =>
        result.Success ? PoseEditResult.Ok(affected) : PoseEditResult.Fail(result.Detail!);

    /// <summary>The bones in this region that carry edits.</summary>
    private static TransformTargetState[] Edited(
        IReadOnlyList<TransformTargetState> states,
        PoseRegion region) =>
        states
            .Where(state =>
                state.Pose.Layers.Count > 0 &&
                MatchesRegion(state.Target.Bone!.Value.CanonicalName, region))
            .ToArray();

    private static TransformTargetState Cleared(TransformTargetState state) =>
        state with { Pose = PoseOperations.Reset(state.Pose) };

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
}
