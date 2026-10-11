using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

/// <summary>Every value a surface sets on a camera, in native units. Portrait
/// is the mode and the roll together: toggling the mode turns the roll, so
/// one step must restore both.</summary>
public static class CameraProperties
{
    public static readonly EntityProperty<CameraId, bool> IsLocked = new("IsLocked", on => on ? "Lock camera" : "Unlock camera");
    public static readonly EntityProperty<CameraId, string> Name = new("Name", "Rename camera");
    public static readonly EntityProperty<CameraId, float> Zoom = new("Zoom", "Set camera zoom");
    public static readonly EntityProperty<CameraId, float> FoV = new("FoV", "Set camera FoV");
    public static readonly EntityProperty<CameraId, float> Roll = new("Roll", "Set camera roll");
    public static readonly EntityProperty<CameraId, Vector2> Angle = new("Angle", "Turn camera");
    public static readonly EntityProperty<CameraId, Vector2> Pan = new("Pan", "Pan camera");
    public static readonly EntityProperty<CameraId, Vector3> PositionOffset = new("PositionOffset", "Move camera");
    public static readonly EntityProperty<CameraId, Vector3?> FixedPosition = new("FixedPosition", point => point is null ? "Unpin camera" : "Pin camera");
    public static readonly EntityProperty<CameraId, Vector3> Position = new("Position", "Move camera");
    public static readonly EntityProperty<CameraId, Vector3> Rotation = new("Rotation", "Turn camera");
    public static readonly EntityProperty<CameraId, bool> DisableCollision = new("DisableCollision", "Set camera collision");
    public static readonly EntityProperty<CameraId, bool> DelimitCamera = new("DelimitCamera", "Set camera limits");
    public static readonly EntityProperty<CameraId, bool> MovementEnabled = new("MovementEnabled", "Set camera movement");
    public static readonly EntityProperty<CameraId, bool> Move2D = new("Move2D", "Set lateral movement");
    public static readonly EntityProperty<CameraId, float> MovementSpeed = new("MovementSpeed", "Set flight speed");
    public static readonly EntityProperty<CameraId, float> MouseSensitivity = new("MouseSensitivity", "Set mouse sensitivity");
    public static readonly EntityProperty<CameraId, bool> DelimitAngle = new("DelimitAngle", "Set angle limit");
    public static readonly EntityProperty<CameraId, bool> Orthographic = new("Orthographic", on => on ? "Orthographic on" : "Orthographic off");
    public static readonly EntityProperty<CameraId, float> OrthographicZoom = new("OrthographicZoom", "Set ortho zoom");
    public static readonly EntityProperty<CameraId, (bool Portrait, float Roll)> Portrait = new("Portrait", "Set camera portrait");
    public static readonly EntityProperty<CameraId, bool> IsTracking = new("IsTracking", on => on ? "Track on" : "Track off");
    public static readonly EntityProperty<CameraId, CameraTrackingMode> TrackingMode = new("TrackingMode", "Set tracking mode");
    public static readonly EntityProperty<CameraId, bool> IsTargetLocked = new("IsTargetLocked", on => on ? "Lock target" : "Unlock target");

    public static readonly IReadOnlyList<EntityProperty> All =
    [
        IsLocked, Name, Zoom, FoV, Roll, Angle, Pan, PositionOffset, FixedPosition, Position, Rotation,
        DisableCollision, DelimitCamera, MovementEnabled, Move2D, MovementSpeed, MouseSensitivity,
        DelimitAngle, Orthographic, OrthographicZoom, Portrait, IsTracking, TrackingMode, IsTargetLocked,
    ];

    /// <summary>A locked camera keeps its framing: it takes no value but the
    /// lock itself. Only the first write is checked; undo and redo replay.</summary>
    public static string? RefuseWhileLocked(IEntityValueTarget<CameraId> camera, EntityProperty property) =>
        property != IsLocked && camera.Read(IsLocked) ? "Unlock the camera first." : null;
}
