using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Presentation;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>Reads and changes the values of objects (props) and world objects,
/// such as position, visibility and tint, as the object panels ask, with each
/// change an undo step.</summary>
public sealed class SceneObjectControl : ISceneObjectControl
{
    private const string PropUnavailable = "The prop is no longer available.";
    private const string ObjectUnavailable = "The object is no longer available.";
    private readonly IEntityBindings _bindings;
    private readonly ValueJournal _journal;
    private readonly IEntityHistoryResolver<IPropHandle> _propHistory;
    private readonly IEntityHistoryResolver<IWorldObject> _objectHistory;
    private readonly EditHistory _history;
    private readonly Dalamud.Plugin.Services.IFramework _framework;
    private readonly EntityValues<PropId> _props;
    private readonly EntityValues<WorldObjectId> _objects;

    public SceneObjectControl(IEntityBindings bindings, ValueJournal journal,
        IEntityHistoryResolver<IPropHandle> propHistory,
        IEntityHistoryResolver<IWorldObject> objectHistory,
        EditHistory history,
        Dalamud.Plugin.Services.IFramework framework)
    {
        _bindings = bindings;
        _journal = journal;
        _propHistory = propHistory;
        _objectHistory = objectHistory;
        _history = history;
        _framework = framework;
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
        return _propHistory.Resolve(original) is { IsValid: true } current ? current : null;
    }

    public PropReading? Read(PropId id) => Resolve(id) is { } p
        ? new(id, p.Name, p.Visible, p.Model, p.Tint) : null;

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

    // Changing a prop's dye respawns it in place, and undo respawns the previous
    // model. An undo step is recorded only if the respawn works.
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

    /// <summary>
    /// Replaces a spawned object's model. A change that works is one undo step,
    /// and undo and redo wait for the model to finish loading. A replacement
    /// that fails or is cancelled records nothing.
    /// </summary>
    public async Task<Outcome> Respawn(WorldObjectId id, string path)
    {
        if (Resolve(id) is not { } o) return new(false, ObjectUnavailable);
        _journal.Seal();
        var before = o.Path;
        var result = await o.Respawn(path);
        if (!result.Success)
            return result;
        void Record()
        {
            var after = o.Path;
            if (!string.Equals(before, after, StringComparison.Ordinal))
                _history.Append(ModelStep(id, o, before, after));
        }
        // The new model can finish loading on another thread. Undo history is
        // only changed on the game's main thread, so the step is recorded there.
        if (_framework.IsInFrameworkUpdateThread) Record();
        else await _framework.RunOnFrameworkThread(Record);
        return result;
    }

    private JournalStep ModelStep(WorldObjectId id, IWorldObject original, string before, string after) =>
        new("Change object model", () => true, () => true)
        {
            AffectedEntities = [SelectionId.ForWorldObject(id)],
            BeforeValue = before,
            AfterValue = after,
            CompleteReplay = (undo, current, cancellation, completed) =>
                _ = ReplayModel(original, undo ? before : after, current, cancellation, completed),
        };

    private async Task ReplayModel(IWorldObject original, string path, Func<bool> current,
        System.Threading.CancellationToken cancellation, Action<Poser.Domain.Transforms.GestureResult> completed)
    {
        Poser.Domain.Transforms.GestureResult result;
        try
        {
            var live = _objectHistory.Resolve(original);
            if (live is not { IsValid: true })
                result = Poser.Domain.Transforms.GestureResult.Fail(ObjectUnavailable);
            else
            {
                var restored = await live.Respawn(path, current, cancellation);
                result = restored.Success
                    ? Poser.Domain.Transforms.GestureResult.Ok()
                    : Poser.Domain.Transforms.GestureResult.Fail(restored.Detail ?? "The model could not be restored.");
            }
        }
        catch (Exception ex)
        {
            result = Poser.Domain.Transforms.GestureResult.Fail(ex.Message);
        }
        void Finish() => completed(result);
        if (_framework.IsInFrameworkUpdateThread) Finish();
        else await _framework.RunOnFrameworkThread(Finish);
    }
}
