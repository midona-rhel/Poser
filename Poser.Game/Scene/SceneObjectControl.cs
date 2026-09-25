using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Scene;

public sealed class SceneObjectControl(
    IEntityBindings bindings, IFramework framework,
    PropSession props, WorldObjectSession objects) : ISceneObjectControl
{
    private IPropHandle? Resolve(PropId id) => framework.IsInFrameworkUpdateThread
        && bindings.Resolve(id) is { Success: true, Value: { IsValid: true } entity } ? entity : null;
    private IWorldObject? Resolve(WorldObjectId id) => framework.IsInFrameworkUpdateThread
        && bindings.Resolve(id) is { Success: true, Value: { IsValid: true } entity } ? entity : null;

    public PropReading? Read(PropId id) => Resolve(id) is { } p
        ? new(id, p.Name, p.Visible, p.Model) : null;

    public WorldObjectReading? Read(WorldObjectId id) => Resolve(id) is { } o
        ? new(id, o.Name, o.Path, o.Spawned, o.Visible, o.IsVfx, o.IsFurniture,
            o.Opacity, o.Tint, o.Dyeable, o.Stain, o.FurnitureLights.ToArray(),
            o.NightState, o.AnimationPaused, o.LoopVfx, o.VfxSpeed, o.VfxPaused, o.VfxIntensity) : null;

    public void Seal() => props.Seal(); // Both sessions share the same value journal.

    private ValueWriteResult Write(PropId id, Action<IPropHandle> change)
    {
        if (Resolve(id) is not { } entity) return new(false, "The prop is no longer available.");
        change(entity);
        return new(true);
    }

    private ValueWriteResult Write(WorldObjectId id, Action<IWorldObject> change)
    {
        if (Resolve(id) is not { } entity) return new(false, "The object is no longer available.");
        change(entity);
        return new(true);
    }

    public ValueWriteResult SetName(PropId id, string value) => Write(id, p => props.SetName(p, value));
    public ValueWriteResult SetVisible(PropId id, bool value) => Write(id, p => props.SetVisible(p, value));
    public ValueWriteResult SetModel(PropId id, PropModel value)
    {
        if (Resolve(id) is not { } p) return new(false, "The prop is no longer available.");
        var changed = props.SetModel(p, value, out var detail);
        return new(changed, detail);
    }

    public ValueWriteResult SetName(WorldObjectId id, string value) => Write(id, o => objects.SetName(o, value));
    public ValueWriteResult SetVisible(WorldObjectId id, bool value) => Write(id, o => objects.SetVisible(o, value));
    public ValueWriteResult SetOpacity(WorldObjectId id, float value) => Write(id, o => objects.SetOpacity(o, value));
    public ValueWriteResult SetTint(WorldObjectId id, Vector3? value) => Write(id, o => objects.SetTint(o, value));
    public ValueWriteResult SetStain(WorldObjectId id, byte value) => Write(id, o => objects.SetStain(o, value));
    public ValueWriteResult SetFurnitureLight(WorldObjectId id, string key, bool value) => Write(id, o => objects.SetFurnitureLight(o, key, value));
    public ValueWriteResult SetNightState(WorldObjectId id, bool value) => Write(id, o => objects.SetNightState(o, value));
    public ValueWriteResult SetAnimationPaused(WorldObjectId id, bool value) => Write(id, o => objects.SetAnimationPaused(o, value));
    public ValueWriteResult SetLoopVfx(WorldObjectId id, bool value) => Write(id, o => objects.SetLoopVfx(o, value));
    public ValueWriteResult SetVfxSpeed(WorldObjectId id, float value) => Write(id, o => objects.SetVfxSpeed(o, value));
    public ValueWriteResult SetVfxPaused(WorldObjectId id, bool value) => Write(id, o => objects.SetVfxPaused(o, value));
    public ValueWriteResult SetVfxIntensity(WorldObjectId id, float value) => Write(id, o => objects.SetVfxIntensity(o, value));

    public async Task<ValueWriteResult> Respawn(WorldObjectId id, string path)
    {
        if (Resolve(id) is not { } o) return new(false, "The object is no longer available.");
        var result = await o.Respawn(path);
        return new(result.Succeeded, result.Detail);
    }

}
