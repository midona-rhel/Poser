using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Posing;

/// <summary>Legacy IEntity presentation bridge into stable-id pose commands.</summary>
public sealed class CleanPoseFacade
{
    private readonly StableBindingRegistry _bindings;
    private readonly PoseEditService _edits;
    private readonly PoseTransferService _transfers;

    public CleanPoseFacade(
        StableBindingRegistry bindings,
        PoseEditService edits,
        PoseTransferService transfers)
    {
        _bindings = bindings;
        _edits = edits;
        _transfers = transfers;
    }

    public bool HasStash => _transfers.HasStash;
    public DateTimeOffset? StashedAt => _transfers.StashedAt;

    public PoseEditResult ResetBone(IBone bone)
    {
        var concrete = bone is VirtualBone group
            ? group.PivotBone
            : bone;
        if (concrete == null || Target(concrete) is not { } target)
            return PoseEditResult.Fail(
                $"Bone {bone.Name} has no stable pose binding.");
        return _edits.Reset(
            new[] { target },
            PoseRegion.All,
            $"Reset {bone.Name}");
    }

    public PoseEditResult Reset(
        ISkeleton skeleton,
        PoseRegion region)
    {
        var targets = Targets(skeleton);
        return _edits.Reset(
            targets,
            region,
            region == PoseRegion.All
                ? "Reset pose"
                : $"Reset {region.ToString().ToLowerInvariant()}");
    }

    public PoseEditResult FlipBone(IBone bone)
    {
        var concrete = bone is VirtualBone group
            ? group.PivotBone
            : bone;
        if (concrete == null || Target(concrete) is not { } target)
            return PoseEditResult.Fail(
                $"Bone {bone.Name} has no stable pose binding.");
        return _edits.Flip(target, $"Flip {bone.Name}");
    }

    public PoseEditResult Mirror(ISkeleton skeleton) =>
        _edits.Mirror(Targets(skeleton), "Mirror pose");

    public PoseCaptureResult Copy(ISkeleton skeleton) =>
        _transfers.Capture(Targets(skeleton));

    public PoseEditResult Paste(
        ISkeleton skeleton,
        PortablePose pose) =>
        _transfers.Apply(Targets(skeleton), pose);

    public PoseEditResult Stash(ISkeleton skeleton) =>
        _transfers.Stash(Targets(skeleton));

    public PoseEditResult ApplyStash(ISkeleton skeleton) =>
        _transfers.ApplyStash(Targets(skeleton));

    private IReadOnlyList<TransformTargetId> Targets(
        ISkeleton skeleton) =>
        skeleton.Bones
            .Where(bone => bone is not VirtualBone)
            .Select(Target)
            .Where(target => target.HasValue)
            .Select(target => target!.Value)
            .ToArray();

    private TransformTargetId? Target(IBone bone) =>
        _bindings.GetBoneId(bone) is { } id
            ? TransformTargetId.ForBone(id)
            : null;
}
