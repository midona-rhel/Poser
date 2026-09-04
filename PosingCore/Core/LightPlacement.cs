using System.Numerics;

namespace Poser.Core;

/// <summary>Shared placement for new lights and the Move to camera command.</summary>
public static class LightPlacement
{
    // A camera-coincident pivot cannot be projected by the world gizmo.
    // Keep a small positive distance, independent of the look vector's length.
    public const float CameraForwardDistance = 1f;

    /// <summary>The caller resolves a nonzero camera look direction, including
    /// any camera-read fallback, before requesting a placement.</summary>
    public static Transform FromCamera(Vector3 cameraPosition, Vector3 lookDirection, Vector3 scale)
    {
        var forward = Vector3.Normalize(lookDirection);
        return new Transform(
            cameraPosition + forward * CameraForwardDistance,
            PoseMath.AlignZTo(forward),
            scale);
    }
}
