using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

/// <summary>Baking an IK chain into the pose; no live bones escape the runtime.</summary>
public interface IIkBake
{
    (TransformTargetId Target, string Text)? Note { get; }
    bool IsPending { get; }
    bool CanBake(TransformTargetId target);
    IReadOnlyList<BoneId> AffectedChain(TransformTargetId target);
    GestureResult Begin(TransformTargetId target);
}
