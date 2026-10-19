using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>Where a Two Joint end bone stood when End rotation was last
/// switched, in the solve frame: the forearm or shin, the end bone, and the
/// user's own end rotation then. Held, the end keeps that rotation; following,
/// it turns as the forearm has turned since. Either way the switch itself
/// moves nothing.</summary>
public readonly record struct IkEndRotationAnchor(Quaternion Forearm, Quaternion End, Quaternion Authored)
{
    public bool IsValid => Valid(Forearm) && Valid(End) && Valid(Authored);

    private static bool Valid(Quaternion value) =>
        TransformMath.IsFinite(value) && value.LengthSquared() > 1e-8f;

    /// <summary>The end bone's rotation now, with the user's rotation since
    /// the switch on top. <paramref name="forearm"/> is the solved forearm
    /// this frame; null while the end is held.</summary>
    public Quaternion Resolve(Quaternion authored, Quaternion? forearm)
    {
        var held = End * Quaternion.Inverse(Authored) * authored;
        return Quaternion.Normalize(forearm is { } turned
            ? turned * Quaternion.Inverse(Forearm) * held
            : held);
    }
}
