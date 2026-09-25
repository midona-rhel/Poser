using System.Numerics;
using Poser.Application.Transforms;
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

/// <summary>Detached camera editor values and exact-generation writes in native units.</summary>
public interface ICameraControl
{
    CameraReading? Read(CameraId id);
    void Seal();
    ValueWriteResult SetName(CameraId id, string value);
    ValueWriteResult SetAngle(CameraId id, Vector2 value);
    ValueWriteResult SetPan(CameraId id, Vector2 value);
    ValueWriteResult SetRoll(CameraId id, float value);
    ValueWriteResult SetZoom(CameraId id, float value);
    ValueWriteResult SetFoV(CameraId id, float value);
    ValueWriteResult SetPositionOffset(CameraId id, Vector3 value);
    ValueWriteResult SetFixedPosition(CameraId id, Vector3? value);
    ValueWriteResult SetDisableCollision(CameraId id, bool value);
    ValueWriteResult SetDelimitCamera(CameraId id, bool value);
    ValueWriteResult SetPosition(CameraId id, Vector3 value);
    ValueWriteResult SetRotation(CameraId id, Vector3 value);
    ValueWriteResult SetMovementEnabled(CameraId id, bool value);
    ValueWriteResult SetMove2D(CameraId id, bool value);
    ValueWriteResult SetMovementSpeed(CameraId id, float value);
    ValueWriteResult SetMouseSensitivity(CameraId id, float value);
    ValueWriteResult SetDelimitAngle(CameraId id, bool value);
    ValueWriteResult SetOrthographic(CameraId id, bool value);
    ValueWriteResult SetOrthographicZoom(CameraId id, float value);
    ValueWriteResult SetPortrait(CameraId id, bool value);
    ValueWriteResult SetLive(CameraId id, bool value);
    ValueWriteResult ResetPosition(CameraId id);
}
