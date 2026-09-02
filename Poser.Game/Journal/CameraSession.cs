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
    private readonly IVirtualCameraService _cameras;
    private readonly IEntityBindings _bindings;

    public CameraSession(ValueJournal journal, IVirtualCameraService cameras, IEntityBindings bindings)
    {
        _journal = journal;
        _cameras = cameras;
        _bindings = bindings;
    }

    public void Seal() => _journal.Seal();

    /// <summary>True when the camera took the value. A locked camera
    /// refuses and nothing is written or journaled.</summary>
    private bool Set<T>(IVirtualCamera c, string property, string description, Func<T> read, Action<T> write, T value)
    {
        if (c.IsLocked)
            return false;
        _journal.Set((c, property), description, read, write, value, () => c.IsValid);
        return true;
    }

    public void SetLocked(IVirtualCamera c, bool v) =>
        _journal.Set((c, "IsLocked"), v ? "Lock camera" : "Unlock camera", () => c.IsLocked, x => c.IsLocked = x, v, () => c.IsValid);

    public bool SetName(IVirtualCamera c, string v) => Set(c, "Name", "Rename camera", () => c.Name, x => c.Name = x, v);
    public bool SetZoom(IVirtualCamera c, float v) => Set(c, "Zoom", "Set camera zoom", () => c.Zoom, x => c.Zoom = x, v);
    public bool SetFoV(IVirtualCamera c, float v) => Set(c, "FoV", "Set camera FoV", () => c.FoV, x => c.FoV = x, v);
    public bool SetRoll(IVirtualCamera c, float v) => Set(c, "Roll", "Set camera roll", () => c.Roll, x => c.Roll = x, v);
    public bool SetAngle(IVirtualCamera c, Vector2 v) => Set(c, "Angle", "Turn camera", () => c.Angle, x => c.Angle = x, v);
    public bool SetPan(IVirtualCamera c, Vector2 v) => Set(c, "Pan", "Pan camera", () => c.Pan, x => c.Pan = x, v);
    public bool SetPositionOffset(IVirtualCamera c, Vector3 v) => Set(c, "PositionOffset", "Move camera", () => c.PositionOffset, x => c.PositionOffset = x, v);
    public bool SetTargetOffset(IVirtualCamera c, Vector3 v) => Set(c, "TargetOffset", "Move camera target", () => c.TargetOffset, x => c.TargetOffset = x, v);
    public bool SetFixedPosition(IVirtualCamera c, Vector3? v) => Set(c, "FixedPosition", v is null ? "Unpin camera" : "Pin camera", () => c.FixedPosition, x => c.FixedPosition = x, v);
    public bool SetPosition(IVirtualCamera c, Vector3 v) => Set(c, "Position", "Move camera", () => c.Position, x => c.Position = x, v);
    public bool SetRotation(IVirtualCamera c, Vector3 v) => Set(c, "Rotation", "Turn camera", () => c.Rotation, x => c.Rotation = x, v);
    public bool SetDisableCollision(IVirtualCamera c, bool v) => Set(c, "DisableCollision", "Set camera collision", () => c.DisableCollision, x => c.DisableCollision = x, v);
    public bool SetDelimitCamera(IVirtualCamera c, bool v) => Set(c, "DelimitCamera", "Set camera limits", () => c.DelimitCamera, x => c.DelimitCamera = x, v);
    public bool SetMovementEnabled(IVirtualCamera c, bool v) => Set(c, "MovementEnabled", "Set camera movement", () => c.MovementEnabled, x => c.MovementEnabled = x, v);
    public bool SetMove2D(IVirtualCamera c, bool v) => Set(c, "Move2D", "Set lateral movement", () => c.Move2D, x => c.Move2D = x, v);
    public bool SetMovementSpeed(IVirtualCamera c, float v) => Set(c, "MovementSpeed", "Set flight speed", () => c.MovementSpeed, x => c.MovementSpeed = x, v);
    public bool SetMouseSensitivity(IVirtualCamera c, float v) => Set(c, "MouseSensitivity", "Set mouse sensitivity", () => c.MouseSensitivity, x => c.MouseSensitivity = x, v);
    public bool SetDelimitAngle(IVirtualCamera c, bool v) => Set(c, "DelimitAngle", "Set angle limit", () => c.DelimitAngle, x => c.DelimitAngle = x, v);
    public bool SetOrthographic(IVirtualCamera c, bool v) => Set(c, "Orthographic", v ? "Orthographic on" : "Orthographic off", () => c.Orthographic, x => c.Orthographic = x, v);
    public bool SetTracking(IVirtualCamera c, bool v) => Set(c, "IsTracking", v ? "Track on" : "Track off", () => c.IsTracking, x => c.IsTracking = x, v);
    public bool SetTrackingMode(IVirtualCamera c, CameraTrackingMode v) => Set(c, "TrackingMode", "Set tracking mode", () => c.TrackingMode, x => c.TrackingMode = x, v);
    public bool SetTargetLocked(IVirtualCamera c, bool v) => Set(c, "IsTargetLocked", v ? "Lock target" : "Unlock target", () => c.IsTargetLocked, x => c.IsTargetLocked = x, v);

    /// <summary>The ortho zoom re-asserts the projection so the width takes
    /// effect at once, as the page always did.</summary>
    public bool SetOrthographicZoom(IVirtualCamera c, float v) =>
        Set(c, "OrthographicZoom", "Set ortho zoom", () => c.OrthographicZoom, x =>
        {
            c.OrthographicZoom = x;
            if (c.Orthographic)
                c.Orthographic = true;
        }, v);

    /// <summary>Back to the spawn position (free) or a zero offset (game).</summary>
    public bool ResetPosition(IVirtualCamera c) =>
        c.Kind == Domain.Scene.CameraKind.Free
            ? SetPosition(c, c.SpawnPosition)
            : SetPositionOffset(c, Vector3.Zero);

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
            if (next is { IsValid: true })
                _cameras.SetLive(next);
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
            next => { camera.PositionOffset = next.Item1; camera.Zoom = next.Item2; },
            () => camera.IsValid);
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
        _journal.Record("Follow actor", before, (ActorId?)actorId, next => PutTarget(c, next), () => c.IsValid);
        return true;
    }

    public void ClearTargetActor(IVirtualCamera c)
    {
        var before = c.TargetActorId;
        _cameras.ClearTargetActor(c);
        _journal.Record("Stop following", before, (ActorId?)null, next => PutTarget(c, next), () => c.IsValid);
    }

    private void PutTarget(IVirtualCamera c, ActorId? target)
    {
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
