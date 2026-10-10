using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Presentation;
using Poser.Services;

namespace Poser.Game.Scene;

public sealed class SceneObjectControl : ISceneObjectControl
{
    private const string PropUnavailable = "The prop is no longer available.";
    private const string ObjectUnavailable = "The object is no longer available.";
    private readonly IEntityBindings _bindings;
    private readonly ValueJournal _journal;
    private readonly IEntityHistoryResolver<IPropHandle>? _propHistory;
    private readonly EntityValues<PropId> _props;
    private readonly EntityValues<WorldObjectId> _objects;

    public SceneObjectControl(IEntityBindings bindings, ValueJournal journal,
        IEntityHistoryResolver<IPropHandle>? propHistory = null,
        IEntityHistoryResolver<IWorldObject>? objectHistory = null)
    {
        _bindings = bindings;
        _journal = journal;
        _propHistory = propHistory;
        _props = new(journal, new HandleValuePort<PropId, IPropHandle>(Resolve, SelectionId.ForProp,
            prop => prop.IsValid, propHistory, SceneObjectAccessors.Props(), PropUnavailable), PropUnavailable);
        _objects = new(journal, new HandleValuePort<WorldObjectId, IWorldObject>(Resolve, SelectionId.ForWorldObject,
            o => o.IsValid, objectHistory, SceneObjectAccessors.WorldObjects(), ObjectUnavailable), ObjectUnavailable);
    }

    private IPropHandle? Resolve(PropId id) =>
        _bindings.Resolve(id) is { Success: true, Value: { IsValid: true } entity } ? entity : null;
    private IWorldObject? Resolve(WorldObjectId id) =>
        _bindings.Resolve(id) is { Success: true, Value: { IsValid: true } entity } ? entity : null;

    private IPropHandle? Current(IPropHandle original)
    {
        var current = _propHistory is null ? original : _propHistory.Resolve(original);
        return current is { IsValid: true } ? current : null;
    }

    public PropReading? Read(PropId id) => Resolve(id) is { } p
        ? new(id, p.Name, p.Visible, p.Model) : null;

    public WorldObjectReading? Read(WorldObjectId id) => Resolve(id) is { } o
        ? new(id, o.Name, o.Path, o.Spawned, o.Visible, o.IsVfx, o.IsFurniture,
            o.Opacity, o.Tint, o.Dyeable, o.Stain, o.FurnitureLights.ToArray(),
            o.NightState, o.AnimationPaused, o.LoopVfx, o.VfxSpeed, o.VfxPaused, o.VfxIntensity) : null;

    public void Seal() => _journal.Seal();

    public Outcome Set<T>(PropId id, EntityProperty<PropId, T> property, T value) =>
        _props.Set(id, property, value);

    public Outcome Update<T>(PropId id, EntityProperty<PropId, T> property, Func<T, T> change) =>
        _props.Update(id, property, change);

    public Outcome Set<T>(WorldObjectId id, EntityProperty<WorldObjectId, T> property, T value) =>
        _objects.Set(id, property, value);

    public Outcome Update<T>(WorldObjectId id, EntityProperty<WorldObjectId, T> property, Func<T, T> change) =>
        _objects.Update(id, property, change);

    // A dye respawns the prop in place; its undo respawns the previous model.
    // Only a landed respawn is a step.
    public Outcome SetModel(PropId id, PropModel model)
    {
        if (Resolve(id) is not { } p) return new(false, PropUnavailable);
        var before = p.Model;
        if (!p.Respawn(model, out var refusal)) return new(false, refusal);
        _journal.Record(SelectionId.ForProp(id), "Change prop model", before, model,
            next => Current(p) is not { } live ? new(false, PropUnavailable)
                : live.Respawn(next, out var why) ? Outcome.Ok() : new(false, why),
            () => Current(p) is not null);
        return Outcome.Ok();
    }

    public Outcome SetFurnitureLight(WorldObjectId id, string key, bool enabled)
    {
        _journal.Seal();
        var result = _objects.Update(id, WorldObjectProperties.FurnitureLights, lights =>
            lights.Select(light => light.Key == key ? light with { Enabled = enabled } : light).ToArray());
        _journal.Seal();
        return result;
    }

    public async Task<Outcome> Respawn(WorldObjectId id, string path)
    {
        if (Resolve(id) is not { } o) return new(false, ObjectUnavailable);
        return await o.Respawn(path);
    }
}
