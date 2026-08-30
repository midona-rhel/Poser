using System;
using Dalamud.Plugin.Services;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// .xivc import/export. Export snapshots the live camera state; import
/// creates a camera of the file's kind and writes the file's every property
/// onto it.
/// </summary>
public class CameraFileService : ICameraFileService
{
    private readonly IPluginLog _log;
    private readonly IVirtualCameraService _cameras;

    public CameraFileService(
        IPluginLog log,
        IVirtualCameraService cameras)
    {
        _log = log;
        _cameras = cameras;
    }

    public bool ExportCamera(IVirtualCamera camera, string path) =>
        ExportCamera(camera, path, null, null);

    public bool ExportCamera(
        IVirtualCamera camera,
        string path,
        PlacementAnchorData? cameraAnchor,
        PlacementAnchorData? actorAnchor)
    {
        try
        {
            var file = CreateCameraFile(camera);
            file.CameraAnchor = cameraAnchor;
            file.ActorAnchor = actorAnchor;
            if (file.Save(path))
            {
                _log.Debug($"Exported camera '{camera.Name}' to {path}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to export camera: {ex.Message}");
            return false;
        }
    }

    public IVirtualCamera? ImportCamera(string path) =>
        ImportCamera(path, ObjectPlacementMode.AsSaved, default, 0f, out _);

    public IVirtualCamera? ImportCamera(
        string path,
        ObjectPlacementMode mode,
        System.Numerics.Vector3 currentPosition,
        float currentYaw,
        out string? refusal)
    {
        refusal = null;
        try
        {
            var file = CameraFile.Load(path);
            if (file == null)
            {
                _log.Error($"Failed to load camera file from {path}");
                refusal = "The camera file could not be read.";
                return null;
            }

            if (mode != ObjectPlacementMode.AsSaved)
            {
                if (file.Kind != CameraKind.Free)
                {
                    refusal = "An orbit camera follows its target, so it " +
                        "cannot be placed relatively. Load it as saved.";
                    return null;
                }
                var anchor = mode == ObjectPlacementMode.RelativeToCamera
                    ? file.CameraAnchor
                    : file.ActorAnchor;
                if (anchor is null)
                {
                    refusal = "This entry records no anchor for that " +
                        "placement. Load it as saved instead.";
                    return null;
                }
                float yawDelta = currentYaw - anchor.Yaw;
                var turn = System.Numerics.Quaternion.CreateFromAxisAngle(
                    System.Numerics.Vector3.UnitY, yawDelta);
                file.Position = currentPosition +
                    System.Numerics.Vector3.Transform(
                        file.Position - anchor.Position, turn);
                // The free camera's heading is its Angle.X; the pitch keeps.
                file.Angle = file.Angle with { X = file.Angle.X + yawDelta };
            }

            var camera = _cameras.CreateCamera(file.Kind);
            if (camera == null)
            {
                _log.Error("Failed to create a camera for the imported file");
                return null;
            }

            Apply(file, camera);
            return camera;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to import camera: {ex.Message}");
            return null;
        }
    }

    /// <summary>The ONE IVirtualCamera → CameraFile mapping; scene capture
    /// reuses it so a scene camera and a .xivc are the same document.</summary>
    internal static CameraFile CreateCameraFile(IVirtualCamera camera) => new()
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
