using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>Scene pose edits. Native actors and skeletons never cross this boundary.</summary>
public interface IPoseCommands
{
    bool HasAuthoredEdits(ActorId actor);
    bool HasStash { get; }
    DateTimeOffset? StashedAt { get; }
    string? StashedFrom { get; }
    PoseEditResult ResetBone(TransformTargetId bone, string name);
    PoseEditResult ResetBones(IReadOnlyList<TransformTargetId> bones, string description);
    PoseEditResult FlipBone(TransformTargetId bone, string name);
    PoseEditResult Reset(ActorId actor, PoseRegion region);
    PoseEditResult Mirror(ActorId actor);
    PoseCaptureResult Copy(ActorId actor);
    PoseEditResult Paste(ActorId actor, PortablePose pose);
    PoseEditResult Stash(ActorId actor, string sourceLabel);
    PoseEditResult ApplyStash(ActorId actor);
}

/// <summary>Cheap authored-layer read; do not capture an entire pose to draw a button.</summary>
public interface IPoseEditReads
{
    bool HasAuthoredEdits(ActorId actor);
}
