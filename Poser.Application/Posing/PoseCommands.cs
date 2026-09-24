using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>Owns pose command scope; PoseEditService owns transactional edits/history.</summary>
public sealed class PoseCommands(
    SceneSession scene, PoseEditService edits, PoseTransferService transfers,
    IPoseEditReads reads, Action<string, PoseEditResult>? report = null) : IPoseCommands
{
    public bool HasStash => transfers.HasStash;
    public DateTimeOffset? StashedAt => transfers.StashedAt;
    public string? StashedFrom => transfers.StashedFrom;
    public bool HasAuthoredEdits(ActorId actor) =>
        scene.Contains(TransformTargetId.ForActor(actor)) && reads.HasAuthoredEdits(actor);

    private PoseEditResult Report(string description, PoseEditResult result)
    {
        report?.Invoke(description, result);
        return result;
    }

    public PoseEditResult ResetBone(TransformTargetId bone, string name) =>
        ResetBones([bone], $"Reset {name}");
    public PoseEditResult ResetBones(IReadOnlyList<TransformTargetId> bones, string description) =>
        Report(description, edits.Reset(bones, PoseRegion.All, description));
    public PoseEditResult FlipBone(TransformTargetId bone, string name) =>
        Report($"Flip {name}", edits.Flip(bone, $"Flip {name}"));

    public PoseEditResult Reset(ActorId actor, PoseRegion region)
    {
        var description = region == PoseRegion.All ? "Reset pose"
            : $"Reset {region.ToString().ToLowerInvariant()}";
        return Report(description, edits.Reset(Targets(actor, region), region, description));
    }

    public PoseEditResult Mirror(ActorId actor)
    {
        // Facing is part of mirror, never of bone-only transfer or reset.
        var targets = Targets(actor).Append(TransformTargetId.ForActor(actor)).ToArray();
        return Report("Mirror edits", edits.Mirror(targets, "Mirror edits"));
    }

    public PoseCaptureResult Copy(ActorId actor) => transfers.Capture(Targets(actor));
    public PoseEditResult Paste(ActorId actor, PortablePose pose) =>
        Report("Paste pose", transfers.Apply(Targets(actor), pose));
    public PoseEditResult Stash(ActorId actor, string sourceLabel) =>
        Report("Stash pose", transfers.Stash(Targets(actor), sourceLabel));
    public PoseEditResult ApplyStash(ActorId actor) =>
        Report("Apply stash", transfers.ApplyStash(Targets(actor)));

    private TransformTargetId[] Targets(ActorId actor, PoseRegion region = PoseRegion.All) =>
        scene.Snapshot.Actors.Where(candidate => candidate.Id == actor)
            .SelectMany(candidate => candidate.Skeletons)
            .Where(skeleton => region == PoseRegion.All || skeleton.Slot == PoseSlot.Character)
            .SelectMany(skeleton => skeleton.Bones)
            .Select(bone => TransformTargetId.ForBone(bone.Id)).ToArray();
}
