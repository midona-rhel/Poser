using System;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

internal sealed class CameraLifecycleSlot
{
    public IVirtualCamera? Live;
    public CameraFile Document = new();
    public bool Locked, TargetLocked, Tracking;
    public IActor? Target;
    public ActorId? TargetId;
    public string TargetName = "";
    public Vector3 TargetOffset;
    public CameraTrackingMode TrackingMode;
    public IBone[] TrackedBones = [];
    public bool HasDocument;
}

/// <summary>Owns virtual-camera lifecycle entries. A removal captures the
/// camera's document plus the targeting and tracking state the document does
/// not carry, so undo brings the camera back as the user last had it.</summary>
internal sealed class CameraLifecycleOwner
{
    private readonly EditHistory _history;
    private readonly IVirtualCameraService _cameras;
    private readonly LifecycleSlotOwner<IVirtualCamera, CameraLifecycleSlot> _slots;

    public CameraLifecycleOwner(EditHistory history, IVirtualCameraService cameras)
    {
        _history = history;
        _cameras = cameras;
        _slots = new(
            camera => new CameraLifecycleSlot { Live = camera },
            slot => slot.Live, (slot, live) => slot.Live = live,
            RemoveCamera, RestoreCamera, retainAliases: true);
    }

    public void Clear() => _slots.Clear();

    public IVirtualCamera? Resolve(IVirtualCamera camera) => _slots.Resolve(camera);

    public void BindReplacement(IVirtualCamera original, IVirtualCamera replacement) =>
        _slots.BindReplacement(original, replacement);

    public IVirtualCamera? CreateCamera(CameraKind kind) =>
        AppendSpawn(
            kind == CameraKind.Free ? "Add free camera" : "Add camera",
            _cameras.CreateCamera(kind));

    public IVirtualCamera? CloneCamera(IVirtualCamera source) =>
        AppendSpawn(
            $"Clone camera '{source.Name}'",
            _cameras.CloneCamera(source));

    public void DestroyCamera(IVirtualCamera camera)
    {
        // The GPose session's own camera cannot be destroyed, so there is
        // nothing to record and nothing to invert.
        if (camera.IsDefault)
        {
            _cameras.DestroyCamera(camera);
            return;
        }
        string description = $"Remove camera '{camera.Name}'";
        var slot = _slots.SlotFor(camera);
        if (!_slots.CaptureAndRemove(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.Restore(slot),
            () => _slots.CaptureAndRemove(slot)));
    }

    private IVirtualCamera? AppendSpawn(
        string description, IVirtualCamera? camera)
    {
        if (camera == null)
            return null;
        var slot = _slots.SlotFor(camera);
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        return camera;
    }

    private bool RemoveCamera(CameraLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) is not { } camera)
            return false;
        if (camera.IsValid)
        {
            slot.Document = Cameras.CameraDocument.Capture(camera);
            slot.Locked = camera.IsLocked;
            slot.Target = camera.TargetActor;
            slot.TargetId = camera.TargetActorId;
            slot.TargetName = camera.TargetActorName;
            slot.TargetOffset = camera.TargetOffset;
            slot.TargetLocked = camera.IsTargetLocked;
            slot.Tracking = camera.IsTracking;
            slot.TrackingMode = camera.TrackingMode;
            slot.TrackedBones = camera.TrackedBones.ToArray();
            slot.HasDocument = true;
            _cameras.DestroyCamera(camera);
        }
        return true;
    }

    private bool RestoreCamera(CameraLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var camera = _cameras.CreateCamera(slot.Document.Kind, makeLive: false);
        if (camera == null)
            return false;
        Cameras.CameraDocument.Apply(slot.Document, camera);
        // A saved file treats zero as unspecified; history owns the exact world origin too.
        camera.Position = slot.Document.Position;
        if (slot.Target is { } target && slot.TargetId is { } targetId)
            _cameras.SetTargetActor(camera, target, targetId, slot.TargetName);
        camera.TargetOffset = slot.TargetOffset;
        camera.IsTargetLocked = slot.TargetLocked;
        foreach (var bone in slot.TrackedBones)
            if (bone.Skeleton.IsValid) camera.TrackedBones.Add(bone);
        camera.TrackingMode = slot.TrackingMode;
        camera.IsTracking = slot.Tracking;
        camera.IsLocked = slot.Locked;
        slot.Live = camera;
        return true;
    }
}
