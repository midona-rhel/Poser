using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Presentation;
using Poser.Services;

namespace Poser.Game.Cameras;

public sealed class CameraControl : ICameraControl
{
    private const string Unavailable = "The camera is no longer available.";
    private const string ControlsUnavailable = "Camera controls are unavailable.";
    private readonly IEntityBindings _bindings;
    private readonly IFramework _framework;
    private readonly IVirtualCameraService _cameras;
    private readonly ValueJournal _journal;
    private readonly IEntityHistoryResolver<IVirtualCamera>? _history;
    private readonly EntityValues<CameraId> _values;

    public CameraControl(IEntityBindings bindings, IFramework framework, IVirtualCameraService cameras,
        ValueJournal journal, IEntityHistoryResolver<IVirtualCamera>? history = null)
    {
        _bindings = bindings;
        _framework = framework;
        _cameras = cameras;
        _journal = journal;
        _history = history;
        // The lock stays reachable while controls are unavailable, as it always was.
        _values = new(journal, new HandleValuePort<CameraId, IVirtualCamera>(Resolve, SelectionId.ForCamera,
            camera => camera.IsValid, history, CameraAccessors.Create(), Unavailable), Unavailable,
            (camera, property) => property != CameraProperties.IsLocked && !cameras.IsAvailable
                ? ControlsUnavailable : CameraProperties.RefuseWhileLocked(camera, property));
    }

    private IVirtualCamera? Resolve(CameraId id) =>
        _bindings.Resolve(id) is { Success: true, Value: { IsValid: true } camera } ? camera : null;

    private IVirtualCamera? Current(IVirtualCamera original)
    {
        var current = _history is null ? original : _history.Resolve(original);
        return current is { IsValid: true } ? current : null;
    }

    public CameraReading? Read(CameraId id) => Resolve(id) is { } c
        ? new(id, _cameras.IsAvailable,
            c.Name, c.Kind, c.IsLive, c.IsDefault, c.IsLocked,
            c.Angle, c.Pan, c.Roll, c.Zoom, c.ZoomLimits, c.FoV,
            c.PositionOffset, c.WorldPosition, c.FixedPosition,
            c.DisableCollision, c.DelimitCamera, c.IsPortraitMode,
            c.Position, c.Rotation, c.MovementEnabled, c.Move2D,
            c.MovementSpeed, c.MouseSensitivity, c.DelimitAngle,
            c.Orthographic, c.OrthographicZoom,
            c.DefaultFoV, c.DefaultRoll, c.DefaultRotation)
        : null;

    public void Seal() => _values.Seal();

    public Outcome Set<T>(CameraId id, EntityProperty<CameraId, T> property, T value) =>
        _values.Set(id, property, value);

    public Outcome Update<T>(CameraId id, EntityProperty<CameraId, T> property, Func<T, T> change) =>
        _values.Update(id, property, change);

    public Outcome Cycle(int delta)
    {
        if (!_framework.IsInFrameworkUpdateThread || !_cameras.IsAvailable)
            return new(false, ControlsUnavailable);
        var list = _cameras.Cameras;
        int current = -1;
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], _cameras.LiveCamera)) { current = i; break; }
        if (current < 0 || list.Count < 2) return Outcome.Ok();
        int next = ((current + delta) % list.Count + list.Count) % list.Count;
        return _bindings.GetCameraId(list[next]) is { } id
            ? SetLive(id, true) : new(false, Unavailable);
    }

    /// <summary>The roll turns with the mode, read from the live camera.</summary>
    public Outcome SetPortrait(CameraId id, bool value) =>
        _values.Update(id, CameraProperties.Portrait, current => (value, current.Roll +
            (current.Portrait == value ? 0f : value ? MathF.PI / 2f : -MathF.PI / 2f)));

    /// <summary>Back to the spawn position (free) or a zero offset (game).</summary>
    public Outcome ResetPosition(CameraId id)
    {
        if (Resolve(id) is not { } camera) return new(false, Unavailable);
        return camera.Kind == CameraKind.Free
            ? _values.Set(id, CameraProperties.Position, camera.SpawnPosition)
            : _values.Set(id, CameraProperties.PositionOffset, Vector3.Zero);
    }

    public Outcome ResetProperties(CameraId id)
    {
        if (Resolve(id) is not { } camera) return new(false, Unavailable);
        if (!_cameras.IsAvailable) return new(false, ControlsUnavailable);
        if (camera.IsLocked) return new(false, "Unlock the camera first.");
        _journal.Seal();
        var before = ResetState.Read(camera);
        camera.ResetProperties();
        var after = ResetState.Read(camera);
        // Keys led by the camera service name no entity: this step stays global.
        _journal.Record((_cameras, camera), "Reset camera properties", before, after, state =>
        {
            if (Current(camera) is not { } live) return new(false, Unavailable);
            CameraTargetControl.PutTarget(_bindings, _cameras, live, state.Target);
            state.Apply(live);
            return Outcome.Ok();
        }, () => Current(camera) is not null);
        return Outcome.Ok();
    }

    // Only fields changed by the native reset belong to this edit. Do not use
    // file import here: it changes the owned reset baseline and other settings.
    private sealed record ResetState(
        Vector3 PositionOffset, Vector3? FixedPosition, Vector3 TargetOffset,
        ActorId? Target, string TargetName, bool TargetLocked,
        bool Collision, bool Delimit, bool Portrait, float Roll, float Zoom,
        float FoV, Vector2 Angle, Vector2 Pan, bool Orthographic,
        float OrthoZoom, float Speed, float Sensitivity)
    {
        public static ResetState Read(IVirtualCamera c) => new(
            c.PositionOffset, c.FixedPosition, c.TargetOffset, c.TargetActorId,
            c.TargetActorName, c.IsTargetLocked, c.DisableCollision, c.DelimitCamera,
            c.IsPortraitMode, c.Roll, c.Zoom, c.FoV, c.Angle, c.Pan, c.Orthographic,
            c.OrthographicZoom, c.MovementSpeed, c.MouseSensitivity);

        public void Apply(IVirtualCamera c)
        {
            c.PositionOffset = PositionOffset;
            c.FixedPosition = FixedPosition;
            c.TargetOffset = TargetOffset;
            c.TargetActorName = TargetName;
            c.IsTargetLocked = TargetLocked;
            c.DisableCollision = Collision;
            c.DelimitCamera = Delimit;
            if (c.IsPortraitMode != Portrait) c.TogglePortraitMode();
            c.Roll = Roll;
            c.Zoom = Zoom;
            c.FoV = FoV;
            c.Angle = Angle;
            c.Pan = Pan;
            c.OrthographicZoom = OrthoZoom;
            c.Orthographic = Orthographic;
            c.MovementSpeed = Speed;
            c.MouseSensitivity = Sensitivity;
        }
    }

    /// <summary>Makes the camera live; the step's undo makes the previous
    /// live camera live again.</summary>
    public Outcome SetLive(CameraId id, bool value)
    {
        if (Resolve(id) is not { } camera) return new(false, Unavailable);
        if (!_cameras.IsAvailable) return new(false, ControlsUnavailable);
        // Lock protects framing, not switching views. Turning off a non-default
        // camera returns to the existing game camera, never creates another.
        var next = value ? camera : camera.IsDefault ? camera :
            _cameras.Cameras.FirstOrDefault(c => c.IsDefault && c.IsValid);
        if (next is null) return new(false, "The main camera is unavailable.");
        var before = _cameras.LiveCamera;
        if (ReferenceEquals(before, next)) return Outcome.Ok();
        _cameras.SetLive(next);
        // Keys led by the camera service name no entity: this step stays global.
        _journal.Record(_cameras, "Switch camera", before, next, ValueWrites.Unchecked<IVirtualCamera?>(target =>
        {
            if (target is not null && Current(target) is { } live)
                _cameras.SetLive(live);
        }));
        return Outcome.Ok();
    }
}
