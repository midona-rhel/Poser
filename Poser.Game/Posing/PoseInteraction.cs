using System.Numerics;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Services;

namespace Poser.Game.Posing;

public sealed class PoseInteraction(
    IEntityBindings bindings,
    IBonePosingService posing, IActorSpawnService spawn) : IPoseInteraction
{
    public bool LinkedBonesEnabled => posing.LinkedBonesEnabled;

    public bool IsCreature(ActorId actor) =>
        bindings.Resolve(actor).Value is { } current &&
        (current.IsCompanion || spawn.GetSpawnedKind(current) is not null);

    public TransformComponents? GetPropagation(SkeletonId skeleton) =>
        bindings.ResolveSkeleton(skeleton) is { } current
            ? posing.GetPoseInfo(current).DefaultPropagation : null;

    public void SetPropagation(SkeletonId skeleton, TransformComponents propagation)
    {
        if (TransformComponentsPolicy.IsDefined(propagation) &&
            bindings.ResolveSkeleton(skeleton) is { } current)
            posing.GetPoseInfo(current).DefaultPropagation = propagation;
    }

    public Vector3 ClampIkTranslation(BoneId bone, Vector3 delta) =>
        bindings.Resolve(bone).Value is { } current
            ? posing.ClampIkTranslation(current, delta) : Vector3.Zero;
}
