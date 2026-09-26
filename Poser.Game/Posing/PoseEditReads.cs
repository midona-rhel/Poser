using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Services;

namespace Poser.Game.Posing;

public sealed class PoseEditReads(
    IEntityBindings bindings, ISkeletonService skeletons, IBonePosingService posing) : IPoseEditReads
{
    public bool HasAuthoredEdits(ActorId id) =>
        bindings.Resolve(id) is { Success: true, Value: { } actor } &&
        skeletons.GetSkeletons(actor).Any(skeleton =>
            posing.GetPoseInfo(skeleton).AllPoses.Any(pose =>
                pose.Stacks.Any(stack => stack.Layer == null)));
}
