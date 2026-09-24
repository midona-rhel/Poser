using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>
/// Every value a surface sets on a camera, as a journal step. A locked
/// camera takes no value and journals nothing; the lock itself is the one
/// value a locked camera still takes.
/// </summary>
public sealed class CameraSession
{
    private readonly ValueJournal _journal;
    private readonly EntityValueJournal<IVirtualCamera> _values;
    private readonly IVirtualCameraService _cameras;
    private readonly IEntityBindings _bindings;

    public CameraSession(ValueJournal journal, IVirtualCameraService cameras, IEntityBindings bindings, IEntityHistoryResolver<IVirtualCamera>? historyResolver = null)
    {
        _journal = journal;
        _cameras = cameras;
        _bindings = bindings;
        _values = new(journal, camera => camera.IsValid, historyResolver);
    }

    public void Seal() => _journal.Seal();

    /// <summary>True when the camera took the value. A locked camera
    /// refuses and nothing is written or journaled.</summary>
    private bool Set<T>(IVirtualCamera c, string property, string description, Func<IVirtualCamera, T> read, Action<IVirtualCamera, T> write, T value)
    {
        if (_values.Current(c) is not { IsLocked: false })
            return false;
        _values.Set(c, property, description, read, write, value);
        return true;
    }

    public void SetLocked(IVirtualCamera c, bool v) =>
        _values.Set(c, "IsLocked", v ? "Lock camera" : "Unlock camera", camera => camera.IsLocked, (camera, x) => camera.IsLocked = x, v);

    public bool SetName(IVirtualCamera c, string v) => Set(c, "Name", "Rename camera", camera => camera.Name, (camera, x) => camera.Name = x, v);
    public bool SetZoom(IVirtualCamera c, float v) => Set(c, "Zoom", "Set camera zoom", camera => camera.Zoom, (camera, x) => camera.Zoom = x, v);
    public bool SetFoV(IVirtualCamera c, float v) => Set(c, "FoV", "Set camera FoV", camera => camera.FoV, (camera, x) => camera.FoV = x, v);
    public bool SetRoll(IVirtualCamera c, float v) => Set(c, "Roll", "Set camera roll", camera => camera.Roll, (camera, x) => camera.Roll = x, v);
    public bool SetAngle(IVirtualCamera c, Vector2 v) => Set(c, "Angle", "Turn camera", camera => camera.Angle, (camera, x) => camera.Angle = x, v);
    public bool SetPan(IVirtualCamera c, Vector2 v) => Set(c, "Pan", "Pan camera", camera => camera.Pan, (camera, x) => camera.Pan = x, v);
    public bool SetPositionOffset(IVirtualCamera c, Vector3 v) => Set(c, "PositionOffset", "Move camera", camera => camera.PositionOffset, (camera, x) => camera.PositionOffset = x, v);
    public bool SetTargetOffset(IVirtualCamera c, Vector3 v) => Set(c, "TargetOffset", "Move camera target", camera => camera.TargetOffset, (camera, x) => camera.TargetOffset = x, v);
    public bool SetFixedPosition(IVirtualCamera c, Vector3? v) => Set(c, "FixedPosition", v is null ? "Unpin camera" : "Pin camera", camera => camera.FixedPosition, (camera, x) => camera.FixedPosition = x, v);
    public bool SetPosition(IVirtualCamera c, Vector3 v) => Set(c, "Position", "Move camera", camera => camera.Position, (camera, x) => camera.Position = x, v);
    public bool SetRotation(IVirtualCamera c, Vector3 v) => Set(c, "Rotation", "Turn camera", camera => camera.Rotation, (camera, x) => camera.Rotation = x, v);
    public bool SetDisableCollision(IVirtualCamera c, bool v) => Set(c, "DisableCollision", "Set camera collision", camera => camera.DisableCollision, (camera, x) => camera.DisableCollision = x, v);
    public bool SetDelimitCamera(IVirtualCamera c, bool v) => Set(c, "DelimitCamera", "Set camera limits", camera => camera.DelimitCamera, (camera, x) => camera.DelimitCamera = x, v);
    public bool SetMovementEnabled(IVirtualCamera c, bool v) => Set(c, "MovementEnabled", "Set camera movement", camera => camera.MovementEnabled, (camera, x) => camera.MovementEnabled = x, v);
    public bool SetMove2D(IVirtualCamera c, bool v) => Set(c, "Move2D", "Set lateral movement", camera => camera.Move2D, (camera, x) => camera.Move2D = x, v);
    public bool SetMovementSpeed(IVirtualCamera c, float v) => Set(c, "MovementSpeed", "Set flight speed", camera => camera.MovementSpeed, (camera, x) => camera.MovementSpeed = x, v);
    public bool SetMouseSensitivity(IVirtualCamera c, float v) => Set(c, "MouseSensitivity", "Set mouse sensitivity", camera => camera.MouseSensitivity, (camera, x) => camera.MouseSensitivity = x, v);
    public bool SetDelimitAngle(IVirtualCamera c, bool v) => Set(c, "DelimitAngle", "Set angle limit", camera => camera.DelimitAngle, (camera, x) => camera.DelimitAngle = x, v);
    public bool SetOrthographic(IVirtualCamera c, bool v) => Set(c, "Orthographic", v ? "Orthographic on" : "Orthographic off", camera => camera.Orthographic, (camera, x) => camera.Orthographic = x, v);
    public bool SetTracking(IVirtualCamera c, bool v) => Set(c, "IsTracking", v ? "Track on" : "Track off", camera => camera.IsTracking, (camera, x) => camera.IsTracking = x, v);
    public bool SetTrackingMode(IVirtualCamera c, CameraTrackingMode v) => Set(c, "TrackingMode", "Set tracking mode", camera => camera.TrackingMode, (camera, x) => camera.TrackingMode = x, v);
    public bool SetTargetLocked(IVirtualCamera c, bool v) => Set(c, "IsTargetLocked", v ? "Lock target" : "Unlock target", camera => camera.IsTargetLocked, (camera, x) => camera.IsTargetLocked = x, v);

    /// <summary>The ortho zoom re-asserts the projection so the width takes
    /// effect at once, as the page always did.</summary>
    public bool SetOrthographicZoom(IVirtualCamera c, float v) =>
        Set(c, "OrthographicZoom", "Set ortho zoom", camera => camera.OrthographicZoom, (camera, x) =>
        {
            camera.OrthographicZoom = x;
            if (camera.Orthographic)
                camera.Orthographic = true;
        }, v);

    /// <summary>Back to the spawn position (free) or a zero offset (game).</summary>
    public bool ResetPosition(IVirtualCamera c) =>
        c.Kind == Domain.Scene.CameraKind.Free
            ? SetPosition(c, c.SpawnPosition)
            : SetPositionOffset(c, Vector3.Zero);

    public bool ResetProperties(IVirtualCamera original)
    {
        if (_values.Current(original) is not { IsLocked: false } camera) return false;
        _journal.Seal();
        var before = ResetState.Read(camera);
        camera.ResetProperties();
        var after = ResetState.Read(camera);
        _journal.Record("Reset camera properties", before, after, state =>
        {
            if (_values.Current(original) is not { } live) return;
            PutTarget(live, state.Target);
            state.Apply(live);
        }, () => _values.Current(original) is not null);
        return true;
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
    public void SetLive(IVirtualCamera c)
    {
        var before = _cameras.LiveCamera;
        if (ReferenceEquals(before, c))
            return;
        _cameras.SetLive(c);
        _journal.Record("Switch camera", before, c, next =>
        {
            if (next is not null && _values.Current(next) is { } live)
                _cameras.SetLive(live);
        });
    }

    /// <summary>Centres the live camera; a landed centre is one step.</summary>
    public CameraCenterResult CenterOnActor(IActor actor) => Center(() => _cameras.CenterOnActor(actor));

    public CameraCenterResult CenterOnBone(IBone bone) => Center(() => _cameras.CenterOnBone(bone));

    private CameraCenterResult Center(Func<CameraCenterResult> center)
    {
        var camera = _cameras.LiveCamera;
        var before = camera is null ? default : (camera.PositionOffset, camera.Zoom);
        var result = center();
        if (!result.Success || camera is null)
            return result;
        _journal.Record(
            "Centre camera", before, (camera.PositionOffset, camera.Zoom),
            next => { if (_values.Current(camera) is { } live) { live.PositionOffset = next.Item1; live.Zoom = next.Item2; } },
            () => _values.Current(camera) is not null);
        return result;
    }

    /// <summary>Follows the actor; the step's undo restores the previous
    /// target, or clears it.</summary>
    public bool SetTargetActor(IVirtualCamera c, IActor actor, ActorId actorId, string displayName)
    {
        if (c.IsLocked)
            return false;
        var before = c.TargetActorId;
        if (!_cameras.SetTargetActor(c, actor, actorId, displayName))
            return false;
        _journal.Record("Follow actor", before, (ActorId?)actorId, next => PutTarget(c, next), () => _values.Current(c) is not null);
        return true;
    }

    public void ClearTargetActor(IVirtualCamera c)
    {
        var before = c.TargetActorId;
        _cameras.ClearTargetActor(c);
        _journal.Record("Stop following", before, (ActorId?)null, next => PutTarget(c, next), () => _values.Current(c) is not null);
    }

    private void PutTarget(IVirtualCamera original, ActorId? target)
    {
        if (_values.Current(original) is not { } c) return;
        if (target is not { } id)
        {
            _cameras.ClearTargetActor(c);
            return;
        }
        var resolved = _bindings.Resolve(id);
        if (!resolved.Success || resolved.Value is not { } actor)
            return;
        _cameras.SetTargetActor(c, actor, id, actor.Name);
    }
}
