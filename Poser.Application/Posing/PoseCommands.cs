using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>
/// Picks the bones each pose command works on (an actor's bones, or one
/// region of them) and logs the result. PoseEditService does the edit.
/// </summary>
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

    // Mirror also turns the actor's facing; reset and stash only touch bones.
    public PoseEditResult Mirror(ActorId actor) =>
        Report("Mirror edits", edits.Mirror(Targets(actor), TransformTargetId.ForActor(actor), "Mirror edits"));

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
