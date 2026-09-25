using Poser.Entities;
using Poser.Files;

namespace Poser.Game.Cameras;

/// <summary>One native camera mapping for files, scene capture and lifecycle history.</summary>
internal static class CameraDocument
{
    /// <summary>The ONE IVirtualCamera → CameraFile mapping; scene capture
    /// reuses it so a scene camera and a .xivc are the same document.</summary>
    internal static CameraFile Capture(IVirtualCamera camera) => new()
    {
        Name = camera.Name,
        Kind = camera.Kind,
        Angle = camera.Angle,
        Pan = camera.Pan,
        Roll = camera.Roll,
        Zoom = camera.Zoom,
        FoV = camera.FoV,
        PositionOffset = camera.PositionOffset,
        FixedPosition = camera.FixedPosition,
        DisableCollision = camera.DisableCollision,
        DelimitCamera = camera.DelimitCamera,
        Position = camera.Position,
        Rotation = camera.Rotation,
        MovementEnabled = camera.MovementEnabled,
        Move2D = camera.Move2D,
        MovementSpeed = camera.MovementSpeed,
        MouseSensitivity = camera.MouseSensitivity,
        DelimitAngle = camera.DelimitAngle,
        Orthographic = camera.Orthographic,
        OrthographicZoom = camera.OrthographicZoom,
    };

    /// <summary>The ONE CameraFile → IVirtualCamera property application;
    /// scene load reuses it.</summary>
    internal static void Apply(CameraFile file, IVirtualCamera camera)
    {
        camera.Name = file.Name;
        camera.Angle = file.Angle;
        camera.Pan = file.Pan;
        camera.Roll = file.Roll;
        camera.Zoom = file.Zoom;
        camera.FoV = file.FoV;
        camera.PositionOffset = file.PositionOffset;
        camera.FixedPosition = file.FixedPosition;
        camera.DisableCollision = file.DisableCollision;
        camera.DelimitCamera = file.DelimitCamera;
        // A free camera keeps the position it spawned at unless the file
        // carries one — Vector3.Zero is "spawn here", not the world origin.
        if (file.Position != System.Numerics.Vector3.Zero)
            camera.Position = file.Position;
        camera.Rotation = file.Rotation;
        camera.MovementEnabled = file.MovementEnabled;
        camera.Move2D = file.Move2D;
        camera.MovementSpeed = file.MovementSpeed;
        camera.MouseSensitivity = file.MouseSensitivity;
        camera.DelimitAngle = file.DelimitAngle;
        camera.Orthographic = file.Orthographic;
        camera.OrthographicZoom = file.OrthographicZoom;
        // A file apply is an ownership moment: what arrived becomes the
        // reset baseline.
        camera.CaptureOwnedDefaults();
    }
}
