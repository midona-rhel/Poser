using System;
using System.Numerics;

namespace Poser.Files;

/// <summary>
/// Where a loaded object entry lands.
/// </summary>
public enum ObjectPlacementMode
{
    /// <summary>The absolute transform the file states.</summary>
    AsSaved,

    /// <summary>Keep the saved offset from the camera: the full 3D offset —
    /// height included — carried onto the CURRENT camera, turned by the yaw
    /// difference only. Pitch and roll never bend the placement, so a light
    /// saved at head height beside the camera comes back at head height
    /// beside today's camera.</summary>
    RelativeToCamera,

    /// <summary>The same rule anchored on the selected actor.</summary>
    RelativeToSelectedActor,

    /// <summary>The content's CENTROID lands a short reach in front of the
    /// current camera, no turn — the light spawn's own behavior,
    /// generalized to every entry and made THE default (ruled 2026-08-31).
    /// Needs no saved anchor, so it is never unavailable.</summary>
    InFrontOfCamera,
}

/// <summary>
/// One placement anchor as a file records it: where the anchor stood and
/// which way it faced around the up axis. Yaw-only on purpose — the anchor's
/// pitch and roll are noise for placement.
/// </summary>
[Serializable]
public class PlacementAnchorData
{
    public Vector3 Position { get; set; }

    /// <summary>Radians about +Y.</summary>
    public float Yaw { get; set; }
}

/// <summary>
/// The one shared answer for where object entries land — the load dialogs
/// and the library tiles are different mounts of the same choice, exactly as
/// the scene load options are. Session state, never persisted.
/// </summary>
public sealed class ObjectPlacementPreferences
{
    public ObjectPlacementMode Mode { get; set; } = ObjectPlacementMode.AsSaved;
}

/// <summary>The pure placement math, shared by every kind that places.</summary>
public static class ObjectPlacement
{
    /// <summary>Yaw of a facing direction about +Y (XIV is Y-up).</summary>
    public static float YawOf(Vector3 forward) =>
        MathF.Atan2(forward.X, forward.Z);

    /// <summary>Yaw of a rotation: where it sends +Z, flattened.</summary>
    public static float YawOf(Quaternion rotation) =>
        YawOf(Vector3.Transform(Vector3.UnitZ, rotation));

    /// <summary>
    /// Carries a saved transform from its saved anchor onto the current one:
    /// the saved offset (all three axes) turns by the yaw difference and
    /// re-attaches at the current position; the saved rotation turns by the
    /// same yaw difference and keeps its own pitch and roll.
    /// </summary>
    public static void Rebase(
        LightFile.TransformData transform,
        PlacementAnchorData saved,
        Vector3 currentPosition,
        float currentYaw)
    {
        float yawDelta = currentYaw - saved.Yaw;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDelta);
        var offset = transform.Position - saved.Position;
        transform.Position = currentPosition + Vector3.Transform(offset, turn);
        transform.Rotation = Quaternion.Normalize(turn * transform.Rotation);
    }
}
