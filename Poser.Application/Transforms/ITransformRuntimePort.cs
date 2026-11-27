using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public interface ITransformRuntimePort
{
    /// <summary>Parents are written before children in one multi-entity edit.</summary>
    int DependencyDepth(TransformTargetId target) => 0;
    TransformPortResult Capture(TransformTargetId target);

    /// <summary>
    /// Writes a new value. A bone's value is worked out from the value read
    /// when the change began. With <paramref name="rawBaseline"/> it is worked
    /// out from the bone's value before face bones are moved onto the head
    /// instead, which is the value a pose file stores; baking an expression
    /// into the pose needs that. A failed write may still have changed
    /// something, so the caller puts back the value read at the start before
    /// it allows another change.
    /// </summary>
    TransformPortResult ApplyAbsolute(
        TransformTargetState baseline,
        PoseTransform desired,
        bool rawBaseline = false);

    /// <summary>
    /// Puts back a value read earlier. A failed restore may still have changed
    /// something, so the caller keeps the value and tries again before the
    /// next change.
    /// </summary>
    TransformPortResult Restore(TransformTargetState state);
}
