using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>What the parenting section in the inspector reads and changes:
/// a thing's parent link, whether it can have one, and attaching or
/// detaching it.</summary>
public interface ITransformParenting
{
    TransformParent? Read(SelectionId child);
    bool CanParent(SelectionId child);
    Outcome Attach(SelectionId child, SelectionId? target);
}
