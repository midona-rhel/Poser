using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Files;
using Poser.Game.WorldObjects;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>What a world-object lifecycle entry has to put back. Captured at
/// removal time so redo restores the last authored state.</summary>
internal readonly record struct WorldObjectState(
    nint Address,
    string Path,
    bool Spawned,
    Transform Placement,
    bool Visible)
{
    public WorldObjectIncarnation Identity { get; init; }
    public string? Name { get; init; }
    public float Opacity { get; init; } = 1f;
    public Vector3? Tint { get; init; }
    public byte Stain { get; init; }
    public FurnitureLightState[] FurnitureLights { get; init; } = [];
    public bool NightState { get; init; }
    public bool AnimationPaused { get; init; }
    public bool LoopVfx { get; init; } = true;
    public float VfxSpeed { get; init; } = 1f;
    public float VfxIntensity { get; init; } = 1f;
    public bool VfxPaused { get; init; }
}

/// <summary>The operations required to release and restore world objects.</summary>
internal interface IWorldObjectLifecycle
{
    IReadOnlyList<object> WorldObjects { get; }
    object? Adopt(nint address);
    bool CanReclaim(WorldObjectState state, out string detail);
    object? Reclaim(WorldObjectState state, out string detail);
    object? Spawn(string path, Transform placement, bool visible);
    bool IsLive(object worldObject);
    bool Release(object worldObject);
    WorldObjectState Read(object worldObject);
    void Apply(object worldObject, WorldObjectState state);
}

internal sealed class WorldObjectServiceLifecycle(WorldObjectService worldObjects) : IWorldObjectLifecycle
{
    public IReadOnlyList<object> WorldObjects => worldObjects.Adopted;

    public object? Adopt(nint address) => worldObjects.Adopt(address);

    public bool CanReclaim(WorldObjectState state, out string detail) =>
        worldObjects.CanReclaimBorrow(state.Address, state.Identity, out detail);

    public object? Reclaim(WorldObjectState state, out string detail) =>
        worldObjects.ReclaimBorrow(state.Address, state.Identity, out detail);

    public object? Spawn(string path, Transform placement, bool visible) =>
        worldObjects.Spawn(path, placement, visible, out _);

    public bool IsLive(object worldObject) => ((AdoptedWorldObject)worldObject).IsValid;

    public bool Release(object worldObject) => worldObjects.Release((AdoptedWorldObject)worldObject);

    public WorldObjectState Read(object worldObject)
    {
        var handle = (AdoptedWorldObject)worldObject;
        return new WorldObjectState(
            handle.Address, handle.Path, handle.Spawned, handle.Transform, handle.Visible)
        {
            Identity = handle.Spawned ? default : handle.Identity,
            Name = handle.Name,
            Opacity = handle.Opacity,
            Tint = handle.Tint,
            Stain = handle.Stain,
            FurnitureLights = handle.FurnitureLights.ToArray(),
            NightState = handle.NightState,
            AnimationPaused = handle.AnimationPaused,
            LoopVfx = handle.LoopVfx,
            VfxSpeed = handle.VfxSpeed,
            VfxIntensity = handle.VfxIntensity,
            VfxPaused = handle.VfxPaused,
        };
    }

    public void Apply(object worldObject, WorldObjectState state)
    {
        var handle = (AdoptedWorldObject)worldObject;
        if (state.Name is { } name)
            handle.Name = name;
        handle.Transform = state.Placement;
        handle.Opacity = state.Opacity;
        handle.Tint = state.Tint;
        handle.Stain = state.Stain;
        handle.FurnitureLights = state.FurnitureLights;
        if (handle.IsVfx)
        {
            handle.LoopVfx = state.LoopVfx;
            handle.VfxSpeed = state.VfxSpeed;
            handle.VfxIntensity = state.VfxIntensity;
            handle.VfxPaused = state.VfxPaused;
        }
        else
        {
            handle.NightState = state.NightState;
            handle.AnimationPaused = state.AnimationPaused;
        }
        handle.Visible = state.Visible;
    }
}

/// <summary>Owns world-object identity slots, authored state, and the exact
/// lifecycle entries built from confirmed native release results.</summary>
internal sealed class WorldObjectLifecycleOwner
{
    private sealed class Slot
    {
        public object? Live;
        public WorldObjectState Document;
        public bool HasDocument;
        public TransformTargetId? Target;
        public HistoryEntry? AcquisitionEntry;
        public string? RestoreFailure;
    }

    /// <summary>Tracks the successful members of a group action. Members
    /// already released on a partial redo stay complete when that redo retries.</summary>
    private sealed class Batch(WorldObjectLifecycleOwner owner, IReadOnlyList<Slot> slots)
    {
        public bool Release()
        {
            bool landed = true;
            foreach (var slot in slots)
            {
                if (owner._slots.CurrentInstance(slot) == null)
                    continue;
                if (!owner._slots.CaptureAndRemove(slot))
                    landed = false;
            }
            return landed;
        }

        public bool Restore() => owner.Restore(slots);
        public string? FailureDetail => RestoreFailure(slots);
        public bool DropOnFailure() => owner.DiscardUnrestorable(slots);
    }

    private readonly TransformHistory _history;
    private readonly IWorldObjectLifecycle _worldObjects;
    private readonly Func<object, TransformTargetId?>? _target;
    private readonly LifecycleSlotOwner<object, Slot> _slots;

    public WorldObjectLifecycleOwner(
        TransformHistory history,
        IWorldObjectLifecycle worldObjects,
        Func<object, TransformTargetId?>? target = null)
    {
        _history = history;
        _worldObjects = worldObjects;
        _target = target;
        _slots = new(
            worldObject => new Slot { Live = worldObject },
            slot => slot.Live, (slot, live) => slot.Live = live,
            CaptureAndRemove, Restore, retainAliases: true,
            history: history, transformTarget: target);
    }

    public int Count => _slots.Count;
    public IReadOnlyList<object> WorldObjects => _worldObjects.WorldObjects;
    public void Clear() => _slots.Clear();

    public IWorldObject? CurrentWorldObject(IWorldObject worldObject) =>
        _slots.Resolve(worldObject) as IWorldObject;

    public object? Adopt(nint address)
    {
        var worldObject = _worldObjects.Adopt(address);
        if (worldObject == null)
            return null;
        AppendAcquisition(worldObject);
        return worldObject;
    }

    public object? Spawn(string path, Transform placement, bool visible)
    {
        var worldObject = _worldObjects.Spawn(path, placement, visible);
        if (worldObject == null)
            return null;
        AppendAcquisition(worldObject);
        return worldObject;
    }

    public IWorldObject? Clone(IWorldObject source)
    {
        var name = EntityNames.Next(source.Name,
            WorldObjects.OfType<IWorldObject>().Select(x => x.Name));
        if (Spawn(source.Path, source.Transform, source.Visible) is not IWorldObject copy)
            return null;
        copy.Name = name;
        copy.Opacity = source.Opacity;
        copy.Tint = source.Tint;
        copy.Stain = source.Stain;
        copy.FurnitureLights = source.FurnitureLights.ToArray();
        if (source.IsVfx)
        {
            copy.LoopVfx = source.LoopVfx;
            copy.VfxSpeed = source.VfxSpeed;
            copy.VfxIntensity = source.VfxIntensity;
            copy.VfxPaused = source.VfxPaused;
        }
        else
            copy.NightState = source.NightState;
        return copy;
    }

    public bool Release(object worldObject)
    {
        var slot = _slots.SlotFor(worldObject);
        if (!_slots.CaptureAndRemove(slot))
            return false;
        AppendRelease("Remove world object", [slot]);
        return true;
    }

    public bool ReleaseAll()
    {
        var slots = _worldObjects.WorldObjects.Select(_slots.SlotFor).ToArray();
        if (slots.Length == 0)
            return true;

        // A refused member remains live with its acquisition entry. Only
        // confirmed releases belong to this one group history action.
        var released = new List<Slot>(slots.Length);
        bool allReleased = true;
        foreach (var slot in slots)
            if (_slots.CaptureAndRemove(slot))
                released.Add(slot);
            else
                allReleased = false;
        if (released.Count != 0)
            AppendRelease(released.Count == 1 ? "Remove world object" : $"Remove {released.Count} world objects", released);
        return allReleased;
    }

    private void AppendAcquisition(object worldObject)
    {
        var slot = _slots.SlotFor(worldObject);
        var entry = new SceneLifecyclePatch(
            "Add world object",
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot))
        {
            FailureDetail = () => slot.RestoreFailure,
            DropOnFailure = () => DiscardUnrestorable(slot),
        };
        slot.AcquisitionEntry = entry;
        _history.Append(entry);
    }

    private void AppendRelease(string description, IReadOnlyList<Slot> slots)
    {
        var batch = new Batch(this, slots.ToArray());
        _history.Append(new SceneLifecyclePatch(
            description, batch.Restore, batch.Release)
        {
            FailureDetail = () => batch.FailureDetail,
            DropOnFailure = batch.DropOnFailure,
        });
    }

    private bool CaptureAndRemove(Slot slot)
    {
        if (_slots.CurrentInstance(slot) is not { } worldObject)
            return false;
        bool wasLive = _worldObjects.IsLive(worldObject);
        WorldObjectState document = default;
        TransformTargetId? target = null;
        if (wasLive)
        {
            document = _worldObjects.Read(worldObject);
            target = _target?.Invoke(worldObject);
        }
        bool released = _worldObjects.Release(worldObject);
        if (!released && wasLive)
            return false;
        if (wasLive)
        {
            slot.Document = document;
            slot.HasDocument = true;
            slot.Target = target;
        }
        return true;
    }

    private bool Restore(IReadOnlyList<Slot> slots)
    {
        foreach (var slot in slots)
            slot.RestoreFailure = null;
        // Validate every borrowed member before restoring any owned member,
        // so a mixed group cannot partially land when one identity is stale.
        foreach (var slot in slots)
            if (_slots.CurrentInstance(slot) == null
                && slot.HasDocument && !slot.Document.Spawned
                && !_worldObjects.CanReclaim(slot.Document, out slot.RestoreFailure))
                return false;
        bool landed = true;
        foreach (var slot in slots)
            landed &= _slots.Restore(slot);
        return landed;
    }

    private bool Restore(Slot slot)
    {
        slot.RestoreFailure = null;
        if (_slots.CurrentInstance(slot) != null)
            return true;
        if (!slot.HasDocument)
            return false;
        object? worldObject;
        if (slot.Document.Spawned)
        {
            worldObject = _worldObjects.Spawn(
                slot.Document.Path, slot.Document.Placement, slot.Document.Visible);
        }
        else
        {
            worldObject = _worldObjects.Reclaim(slot.Document, out slot.RestoreFailure);
        }
        if (worldObject == null)
            return false;
        _worldObjects.Apply(worldObject, slot.Document);
        slot.Live = worldObject;
        return true;
    }

    private static string? RestoreFailure(IReadOnlyList<Slot> slots) =>
        slots.Select(slot => slot.RestoreFailure)
            .FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail));

    private bool DiscardUnrestorable(Slot slot)
    {
        if (!slot.HasDocument || slot.Document.Spawned
            || string.IsNullOrWhiteSpace(slot.RestoreFailure))
            return false;
        if (slot.AcquisitionEntry is { } acquisition)
            _history.Drop(acquisition);
        if (slot.Target is { } target)
            _history.DropTransformsFor(target);
        return true;
    }

    private bool DiscardUnrestorable(IReadOnlyList<Slot> slots)
    {
        if (RestoreFailure(slots) == null)
            return false;
        foreach (var slot in slots)
        {
            if (slot.AcquisitionEntry is { } acquisition)
                _history.Drop(acquisition);
            if (slot.Target is { } target)
                _history.DropTransformsFor(target);
        }
        return true;
    }
}
