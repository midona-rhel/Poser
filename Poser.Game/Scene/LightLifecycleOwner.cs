using System;
using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Scene;

internal sealed class LightLifecycleSlot
{
    public ILight? Live;
    public LightFile Document = new();
    public Poser.Entities.IBone? AttachedBone;
    public WorldLightCandidate? Source;
    public bool HasDocument;
}

/// <summary>Owns light lifecycle capture, removal, restoration, and identity
/// rebinding. Historical wrappers alias the slot so property edits continue
/// to target its current light after undo restores a new wrapper.</summary>
internal sealed class LightLifecycleOwner
{
    private readonly TransformHistory _history;
    private readonly ILightingService _lighting;
    private readonly LifecycleSlotOwner<ILight, LightLifecycleSlot> _slots;

    public LightLifecycleOwner(
        TransformHistory history,
        ILightingService lighting,
        Func<ILight, TransformTargetId?>? lightTarget = null)
    {
        _history = history;
        _lighting = lighting;
        _slots = new(
            light => new LightLifecycleSlot { Live = light, Source = _lighting.GetWorldSource(light) },
            slot => slot.Live, (slot, live) => slot.Live = live,
            CaptureAndRemoveCore, RestoreCore, retainAliases: true,
            history: history, transformTarget: lightTarget);
    }

    public LightLifecycleSlot SlotFor(ILight light) => _slots.SlotFor(light);

    public int Count => _slots.Count;

    public bool TryGetSlot(ILight light, out LightLifecycleSlot slot) =>
        _slots.TryGetSlot(light, out slot!);

    public ILight? CurrentInstance(LightLifecycleSlot slot) => _slots.CurrentInstance(slot);

    public bool CaptureAndRemove(LightLifecycleSlot slot) => _slots.CaptureAndRemove(slot);

    public bool Restore(LightLifecycleSlot slot) => _slots.Restore(slot);

    public ILight? CurrentLight(ILight light) =>
        _slots.Resolve(light);

    public void Clear() => _slots.Clear();

    public ILight? SpawnLight(LightKind kind) =>
        RecordSpawn($"Add {KindName(kind)} light", () => _lighting.SpawnLight(kind));

    public ILight? CloneLight(ILight source) =>
        RecordSpawn($"Clone light '{source.Name}'", () => _lighting.CloneLight(source));

    public ILight? AcquireWorldLight(WorldLightCandidate source) =>
        AppendSpawn("Acquire world light", _lighting.CaptureWorldLight(source));

    public ILight? RecordSpawnedLight(string description, ILight? light) =>
        AppendSpawn(description, light);

    public void DestroyLight(ILight light)
    {
        if (!light.IsValid)
            return;
        if (!_lighting.IsSpawnedLight(light) && _lighting.GetWorldSource(light) is null)
        {
            _lighting.DestroyLight(light);
            return;
        }

        string description = $"{(light.Ownership == LightOwnership.World ? "Release" : "Remove")} light '{light.Name}'";
        var slot = SlotFor(light);
        if (!CaptureAndRemove(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => Restore(slot),
            () => CaptureAndRemove(slot)));
    }

    public bool CaptureAndRemove(IReadOnlyList<LightLifecycleSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= CaptureAndRemove(slot);
        return landed;
    }

    public bool Restore(IReadOnlyList<LightLifecycleSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= Restore(slot);
        return landed;
    }

    private ILight? RecordSpawn(string description, Func<ILight?> spawn) =>
        AppendSpawn(description, spawn());

    private ILight? AppendSpawn(string description, ILight? light)
    {
        if (light == null)
            return null;
        var slot = SlotFor(light);
        _history.Append(new SceneLifecyclePatch(
            description,
            () => CaptureAndRemove(slot),
            () => Restore(slot)));
        return light;
    }

    private bool CaptureAndRemoveCore(LightLifecycleSlot slot)
    {
        if (CurrentInstance(slot) is not { } light)
            return false;
        if (light.IsValid)
        {
            // Capture the state at removal time so redo restores the last edit.
            slot.Document = LightFileService.CreateLightFile(light);
            slot.AttachedBone = light.AttachedBone;
            slot.HasDocument = true;
            _lighting.DestroyLight(light);
        }
        return true;
    }

    private bool RestoreCore(LightLifecycleSlot slot)
    {
        if (CurrentInstance(slot) is { IsValid: true })
            return true;
        if (!slot.HasDocument)
            return false;
        var light = slot.Source is { } source
            ? _lighting.CaptureWorldLight(source)
            : _lighting.SpawnLight(slot.Document.Kind);
        if (light == null)
            return false;
        LightFileService.Apply(slot.Document, light);
        ApplyGobo(slot.Document.Gobo, light);
        if (slot.AttachedBone is { Skeleton.IsValid: true } bone)
            light.AttachedBone = bone;
        slot.Live = light;
        return true;
    }

    private void ApplyGobo(string? path, ILight light)
    {
        if (string.IsNullOrEmpty(path))
        {
            _lighting.ClearGobo(light);
            return;
        }
        foreach (var gobo in _lighting.Gobos)
            if (string.Equals(gobo.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _lighting.ApplyGobo(light, gobo);
                return;
            }
        _lighting.ApplyGobo(light, new GoboEntry(path, path));
    }

    private static string KindName(LightKind kind) => kind switch
    {
        LightKind.Point => "point",
        LightKind.Area => "area",
        LightKind.Directional => "directional",
        _ => "spot",
    };
}
