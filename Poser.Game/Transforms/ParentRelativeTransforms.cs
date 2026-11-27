using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Game.Transforms;

/// <summary>Moves a thing that has a parent by changing its offset from that
/// parent, so a drag, a typed value or a group move keeps it attached. Undo
/// puts the offset back the same way. Things without a parent are read and
/// written in the game unchanged.</summary>
public sealed class ParentRelativeTransforms(ITransformRuntimePort inner, TransformParenting parents) : ITransformRuntimePort
{
    public int DependencyDepth(TransformTargetId target) => parents.Depth(target.ToSelectionId());
    public TransformPortResult Capture(TransformTargetId target)
    {
        var result = inner.Capture(target);
        return result.State is { } state ? result with
        { State = state with { Parent = parents.Read(target.ToSelectionId()) } } : result;
    }

    public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired, bool rawBaseline = false)
    {
        var id = baseline.Target.ToSelectionId();
        if (parents.Read(id) == null) return inner.ApplyAbsolute(baseline, desired, rawBaseline);
        return desired.IsValid && parents.EditWorld(id, desired) ? TransformPortResult.Ok()
            : TransformPortResult.Fail(TransformPortStatus.Rejected, "The parent is unavailable; detach before moving this entity.");
    }

    public TransformPortResult Restore(TransformTargetState state) => state.Parent is { } link
        ? parents.RestoreOffset(state.Target.ToSelectionId(), link) ? TransformPortResult.Ok()
            : TransformPortResult.Fail(TransformPortStatus.Rejected, "The transform's parent is unavailable.")
        : inner.Restore(state);
}
