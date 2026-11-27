using Poser.Domain.Identity;

namespace Poser.Application.Selection;

/// <summary>The selection the windows show and edit. Scene things of any kind
/// can be selected together; selected bones belong to one actor.</summary>
public sealed class CurrentSelection
{
    private readonly SelectionScope _live;

    public CurrentSelection()
    {
        _live = new SelectionScope(PublishLiveChanged);
    }

    public event Action<IReadOnlyList<SelectionId>>? SelectionChanged;

    /// <summary>The selection itself, for code that changes it directly.</summary>
    public SelectionScope Live => _live;

    /// <summary>The selected things, first one first.</summary>
    public IReadOnlyList<SelectionId> Selected => Target.Selected;

    public SelectionId? Primary => Target.Primary;

    /// <summary>The actor the primary selection edits: the actor, a bone's
    /// owner, or a gaze anchor's owner.</summary>
    public ActorId? PrimaryActor => Primary?.OwningActor;

    public SelectionId? Anchor => Target.Anchor;

    private SelectionScope Target => _live;

    public bool IsSelected(SelectionId id) => Target.IsSelected(id);

    public void Select(SelectionId id) => Target.Select(id);

    public void Add(SelectionId id) => Target.Add(id);

    public void Toggle(SelectionId id) => Target.Toggle(id);

    public void Remove(SelectionId id) => Target.Remove(id);

    public void RemoveActorLineage(Guid lineage) =>
        Target.RemoveActorLineage(lineage);

    public void Promote(SelectionId id) => Target.Promote(id);

    public void SelectRange(
        SelectionId from,
        SelectionId to,
        IReadOnlyList<SelectionId> displayOrder) =>
        Target.SelectRange(from, to, displayOrder);

    public void Clear() => Target.Clear();

    internal void Reconcile(Func<SelectionId, SelectionId?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (_live.Reconcile(resolver))
            SelectionChanged?.Invoke(_live.Selected.ToArray());
    }

    private void PublishLiveChanged()
    {
        SelectionChanged?.Invoke(_live.Selected.ToArray());
    }
}
