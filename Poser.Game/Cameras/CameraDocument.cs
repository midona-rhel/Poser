using Poser.Documents.Files;
using Poser.Game.Entities;

namespace Poser.Game.Cameras;

/// <summary>One native camera mapping for files, scene capture and lifecycle history.</summary>
internal static class CameraDocument
{
    internal static CameraFile CaptureScene(IVirtualCamera camera)
    {
        var file = Capture(camera);
        // An orbit shot is a one-time placement, not a permanent eye pin.
        if (camera.Kind != Poser.Domain.Scene.CameraKind.Free && camera.FixedPosition is null)
            file.OrbitPosition = camera.WorldPosition;
        return file;
    }

    /// <summary>Maps a virtual camera to the document format shared by scenes and .xivc files.</summary>
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

    /// <summary>Applies the camera document format used by both file and scene loads.</summary>
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
        if (camera is VirtualCamera orbit)
            orbit.PendingOrbitPosition = file.OrbitPosition;
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
        // Reset should restore the values that were just loaded.
        camera.CaptureOwnedDefaults();
    }
}
