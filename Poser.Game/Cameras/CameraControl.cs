using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Cameras;

public sealed class CameraControl(
    IEntityBindings bindings, IFramework framework,
    IVirtualCameraService cameras, CameraSession values) : ICameraControl
{
    private IVirtualCamera? Resolve(CameraId id) => framework.IsInFrameworkUpdateThread &&
        bindings.Resolve(id) is { Success: true, Value: { IsValid: true } camera } &&
        bindings.GetCameraId(camera) == id ? camera : null;

    public CameraReading? Read(CameraId id) => Resolve(id) is { } c
        ? new(id, cameras.IsAvailable,
            c.Name, c.Kind, c.IsLive, c.IsDefault, c.IsLocked,
            c.Angle, c.Pan, c.Roll, c.Zoom, c.ZoomLimits, c.FoV,
            c.PositionOffset, c.WorldPosition, c.FixedPosition,
            c.DisableCollision, c.DelimitCamera, c.IsPortraitMode,
            c.Position, c.Rotation, c.MovementEnabled, c.Move2D,
            c.MovementSpeed, c.MouseSensitivity, c.DelimitAngle,
            c.Orthographic, c.OrthographicZoom,
            c.DefaultFoV, c.DefaultRoll, c.DefaultRotation)
        : null;

    public void Seal() => values.Seal();

    public ValueWriteResult Cycle(int delta)
    {
        if (!framework.IsInFrameworkUpdateThread || !cameras.IsAvailable)
            return new(false, "Camera controls are unavailable.");
        var list = cameras.Cameras;
        int current = -1;
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], cameras.LiveCamera)) { current = i; break; }
        if (current < 0 || list.Count < 2) return ValueWriteResult.Ok();
        int next = ((current + delta) % list.Count + list.Count) % list.Count;
        return bindings.GetCameraId(list[next]) is { } id
            ? SetLive(id, true) : new(false, "The camera is no longer available.");
    }
    public ValueWriteResult ResetProperties(CameraId id) => Edit(id, values.ResetProperties);
    public ValueWriteResult SetLocked(CameraId id, bool value)
    {
        if (Resolve(id) is not { } camera) return new(false, "The camera is no longer available.");
        values.SetLocked(camera, value);
        return ValueWriteResult.Ok();
    }

    private ValueWriteResult Edit(CameraId id, Func<IVirtualCamera, bool> write)
    {
        if (Resolve(id) is not { } camera) return new(false, "The camera is no longer available.");
        if (!cameras.IsAvailable) return new(false, "Camera controls are unavailable.");
        if (camera.IsLocked) return new(false, "Unlock the camera first.");
        return write(camera) ? ValueWriteResult.Ok() : new(false, "The camera edit was refused.");
    }

    public ValueWriteResult SetName(CameraId id, string value) => Edit(id, c => values.SetName(c, value));
    public ValueWriteResult SetAngle(CameraId id, Vector2 value) => Edit(id, c => values.SetAngle(c, value));
    public ValueWriteResult SetPan(CameraId id, Vector2 value) => Edit(id, c => values.SetPan(c, value));
    public ValueWriteResult SetRoll(CameraId id, float value) => Edit(id, c => values.SetRoll(c, value));
    public ValueWriteResult SetZoom(CameraId id, float value) => Edit(id, c => values.SetZoom(c, value));
    public ValueWriteResult SetFoV(CameraId id, float value) => Edit(id, c => values.SetFoV(c, value));
    public ValueWriteResult SetPositionOffset(CameraId id, Vector3 value) => Edit(id, c => values.SetPositionOffset(c, value));
    public ValueWriteResult SetFixedPosition(CameraId id, Vector3? value) => Edit(id, c => values.SetFixedPosition(c, value));
    public ValueWriteResult SetDisableCollision(CameraId id, bool value) => Edit(id, c => values.SetDisableCollision(c, value));
    public ValueWriteResult SetDelimitCamera(CameraId id, bool value) => Edit(id, c => values.SetDelimitCamera(c, value));
    public ValueWriteResult SetPosition(CameraId id, Vector3 value) => Edit(id, c => values.SetPosition(c, value));
    public ValueWriteResult SetRotation(CameraId id, Vector3 value) => Edit(id, c => values.SetRotation(c, value));
    public ValueWriteResult SetMovementEnabled(CameraId id, bool value) => Edit(id, c => values.SetMovementEnabled(c, value));
    public ValueWriteResult SetMove2D(CameraId id, bool value) => Edit(id, c => values.SetMove2D(c, value));
    public ValueWriteResult SetMovementSpeed(CameraId id, float value) => Edit(id, c => values.SetMovementSpeed(c, value));
    public ValueWriteResult SetMouseSensitivity(CameraId id, float value) => Edit(id, c => values.SetMouseSensitivity(c, value));
    public ValueWriteResult SetDelimitAngle(CameraId id, bool value) => Edit(id, c => values.SetDelimitAngle(c, value));
    public ValueWriteResult SetOrthographic(CameraId id, bool value) => Edit(id, c => values.SetOrthographic(c, value));
    public ValueWriteResult SetOrthographicZoom(CameraId id, float value) => Edit(id, c => values.SetOrthographicZoom(c, value));
    public ValueWriteResult SetPortrait(CameraId id, bool value) => Edit(id, c => values.SetPortrait(c, value));
    public ValueWriteResult ResetPosition(CameraId id) => Edit(id, values.ResetPosition);

    public ValueWriteResult SetLive(CameraId id, bool value)
    {
        if (Resolve(id) is not { } camera) return new(false, "The camera is no longer available.");
        if (!cameras.IsAvailable) return new(false, "Camera controls are unavailable.");
        // Lock protects framing, not switching views. Turning off a non-default
        // camera returns to the existing game camera, never creates another.
        var next = value ? camera : camera.IsDefault ? camera :
            cameras.Cameras.FirstOrDefault(c => c.IsDefault && c.IsValid);
        if (next is null) return new(false, "The main camera is unavailable.");
        values.SetLive(next);
        return ValueWriteResult.Ok();
    }
}
