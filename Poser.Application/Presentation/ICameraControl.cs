using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

public sealed record CameraReading(
    CameraId Id, bool Available,
    string Name,
    CameraKind Kind,
    bool IsLive,
    bool IsDefault,
    bool IsLocked,
    Vector2 Angle,
    Vector2 Pan,
    float Roll,
    float Zoom,
    Vector2 ZoomLimits,
    float FoV,
    Vector3 PositionOffset,
    Vector3 WorldPosition,
    Vector3? FixedPosition,
    bool DisableCollision,
    bool DelimitCamera,
    bool IsPortraitMode,
    Vector3 Position,
    Vector3 Rotation,
    bool MovementEnabled,
    bool Move2D,
    float MovementSpeed,
    float MouseSensitivity,
    bool DelimitAngle,
    bool Orthographic,
    float OrthographicZoom,
    float DefaultFoV,
    float DefaultRoll,
    Vector3 DefaultRotation);

/// <summary>Detached camera editor values, exact-generation writes in native
/// units, and the camera commands that are not one property.</summary>
public interface ICameraControl
{
    CameraReading? Read(CameraId id);
    void Seal();
    Outcome Set<T>(CameraId id, EntityProperty<CameraId, T> property, T value);
    Outcome Update<T>(CameraId id, EntityProperty<CameraId, T> property, Func<T, T> change);
    Outcome Cycle(int delta);
    Outcome ResetProperties(CameraId id);
    /// <summary>Quarter-turns the roll with the mode, as one step.</summary>
    Outcome SetPortrait(CameraId id, bool value);
    Outcome SetLive(CameraId id, bool value);
    Outcome ResetPosition(CameraId id);
}
