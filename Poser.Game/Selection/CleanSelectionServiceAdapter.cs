using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Selection;

/// <summary>
/// Legacy IEntity-facing adapter backed by the clean stable-id selection session.
/// </summary>
public sealed class CleanSelectionServiceAdapter : ISelectionService, IDisposable
{
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly IEventBus _events;
    private readonly Dictionary<SelectionId, IEntity> _external = new();
    private readonly List<IEntity> _resolved = new();

    public CleanSelectionServiceAdapter(
        SelectionSession selection,
        StableBindingRegistry bindings,
        IEventBus events,
        CleanSceneLifecycle lifecycle)
    {
        _selection = selection;
        _bindings = bindings;
        _events = events;
        _selection.SelectionChanged += OnSelectionChanged;
        _ = lifecycle;
        RebuildResolved();
    }

    public IReadOnlyList<IEntity> Selected => _resolved;
    public IEntity? Primary => _resolved.FirstOrDefault();
    public IEntity? LastClicked =>
        _selection.Anchor is { } anchor
            ? Resolve(anchor)
            : null;

    public void Select(IEntity entity)
    {
        var id = Bind(entity);
        if (id != null)
            _selection.Select(id.Value);
    }

    public void AddToSelection(IEntity entity)
    {
        var id = Bind(entity);
        if (id != null)
            _selection.Add(id.Value);
    }

    public void RemoveFromSelection(IEntity entity)
    {
        var id = Bind(entity);
        if (id != null)
            _selection.Remove(id.Value);
    }

    public void ToggleSelection(IEntity entity)
    {
        var id = Bind(entity);
        if (id != null)
            _selection.Toggle(id.Value);
    }

    public void SelectRange(
        IEntity from,
        IEntity to,
        IEnumerable<IEntity> displayOrder)
    {
        var fromId = Bind(from);
        var toId = Bind(to);
        if (fromId == null || toId == null)
            return;
        var order = displayOrder
            .Select(Bind)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
        _selection.SelectRange(
            fromId.Value,
            toId.Value,
            order);
    }

    public void ClearSelection() => _selection.Clear();
    public bool IsSelected(IEntity entity) =>
        Bind(entity) is { } id && _selection.IsSelected(id);

    public IEnumerable<T> GetSelected<T>() where T : IEntity =>
        _resolved.OfType<T>();

    public T? GetFirstSelected<T>() where T : class, IEntity =>
        _resolved.OfType<T>().FirstOrDefault();

    public void Dispose()
    {
        _selection.SelectionChanged -= OnSelectionChanged;
    }

    private SelectionId? Bind(IEntity entity)
    {
        switch (entity)
        {
            case IActor actor:
                return _bindings.GetActorId(actor) is { } actorId
                    ? SelectionId.ForActor(actorId)
                    : null;
            case IBone bone when bone is not VirtualBone:
                return _bindings.GetBoneId(bone) is { } boneId
                    ? SelectionId.ForBone(boneId)
                    : null;
            case VirtualBone group:
                if (_bindings.GetActorId(group.Skeleton.Actor) is not { } owner)
                    return null;
                var groupId = SelectionId.ForBoneGroup(
                    owner,
                    group.Id.Unique);
                _external[groupId] = group;
                return groupId;
            default:
                return null;
        }
    }

    private IEntity? Resolve(SelectionId id)
    {
        if (id.Actor is { } actor)
            return _bindings.Resolve(actor).Value;
        if (id.Bone is { } bone)
            return _bindings.Resolve(bone).Value;
        return _external.TryGetValue(id, out var external) &&
               external.Id.Unique == id.ExternalId
            ? external
            : null;
    }

    private void OnSelectionChanged(IReadOnlyList<SelectionId> selected)
    {
        RebuildResolved();
        _events.Publish(new SelectionChangedEvent(_resolved.ToArray()));
        _events.Publish(new BoneSelectionChangedEvent(
            _resolved.OfType<IBone>().FirstOrDefault()));
    }

    private void RebuildResolved()
    {
        var next = _selection.Selected
            .Select(Resolve)
            .Where(entity => entity != null)
            .Cast<IEntity>()
            .ToArray();

        foreach (var removed in _resolved.Except(next))
        {
            removed.IsSelected = false;
            removed.OnDeselected();
        }
        foreach (var added in next.Except(_resolved))
        {
            added.IsSelected = true;
            added.OnSelected();
        }
        _resolved.Clear();
        _resolved.AddRange(next);
    }
}
