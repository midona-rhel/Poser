using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Presentation;
using Poser.Services;

namespace Poser.Game.Overlays;

public sealed class OverlayControl : IOverlayControl
{
    private const string Unavailable = "The overlay is no longer available.";
    private readonly IEntityBindings _bindings;
    private readonly EntityValues<OverlayId> _values;

    public OverlayControl(IEntityBindings bindings, ValueJournal journal,
        IEntityHistoryResolver<IOverlayNode>? history = null)
    {
        _bindings = bindings;
        _values = new(journal, new HandleValuePort<OverlayId, IOverlayNode>(Resolve, SelectionId.ForOverlay,
            node => node.IsValid, history, OverlayAccessors.Create(), Unavailable), Unavailable);
    }

    private IOverlayNode? Resolve(OverlayId id) =>
        _bindings.Resolve(id) is { Success: true, Value: { IsValid: true } node } ? node : null;

    public OverlayReading? Read(OverlayId id) => Resolve(id) is { } node
        ? new(id, node.State) : null;

    public void Seal() => _values.Seal();

    public ValueWriteResult Set<T>(OverlayId id, EntityProperty<OverlayId, T> property, T value) =>
        _values.Set(id, property, value);

    public ValueWriteResult Update<T>(OverlayId id, EntityProperty<OverlayId, T> property, Func<T, T> change) =>
        _values.Update(id, property, change);

    // The collider is read through the bound node, never a UI snapshot: a
    // delayed edit must not replay over a newer transform or collision setting.
    public ValueWriteResult EditCollider(OverlayId id, Func<IkCollider, IkCollider> change)
    {
        if (Resolve(id) is not { State.Collider: not null })
            return new(false, "The collider is no longer available.");
        return _values.Update(id, OverlayProperties.Collider, current => current is null ? null : change(current));
    }
}
