using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public interface ITransformRuntimePort
{
    TransformPortResult Capture(TransformTargetId target);

    /// <summary>
    /// Applies an absolute value. For bones the application basis is the
    /// captured baseline transform; <paramref name="rawBaseline"/> uses
    /// the bone's CURRENT LastRawTransform instead — the pre-reparent
    /// absolute a pose file stores, which diverges from LastTransform on
    /// face partials. The facial bake requires the raw basis.
    /// Failure does not promise that no mutation occurred; callers restore
    /// the captured baseline before accepting another mutation.
    /// </summary>
    TransformPortResult ApplyAbsolute(
        TransformTargetState baseline,
        PoseTransform desired,
        bool rawBaseline = false);

    /// <summary>
    /// Restores one exact captured state. Failure does not promise that no
    /// mutation occurred; Application treats it as mutation-unknown and keeps
    /// the requested state as typed retry evidence.
    /// </summary>
    TransformPortResult Restore(TransformTargetState state);
}
