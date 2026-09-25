using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>Live posing settings and interaction math, resolved by exact identity.</summary>
public interface IPoseInteraction
{
    bool LinkedBonesEnabled { get; }
    bool IsCreature(ActorId actor);
    TransformComponents? GetPropagation(SkeletonId skeleton);
    void SetPropagation(SkeletonId skeleton, TransformComponents propagation);
    Vector3 ClampIkTranslation(BoneId bone, Vector3 delta);
}
