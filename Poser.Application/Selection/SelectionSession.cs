using System.Collections.ObjectModel;
using Poser.Domain.Identity;

namespace Poser.Application.Selection;

/// <summary>
/// Explicit ordered selection state for a live or scoped view.
/// </summary>
/// <remarks>
/// This type owns selection mutation and reconciliation rules so callers can
/// operate on a scope directly without entering a session compatibility
/// adapter. A scope has no ambient link to any other scope.
/// </remarks>
public sealed class SelectionScope
{
    private readonly List<SelectionId> _selected = new();
    private readonly ReadOnlyCollection<SelectionId> _selectedView;
    private SelectionId? _anchor;
    private readonly Action? _changed;

    public SelectionScope(SelectionId seed)
    {
        _selectedView = _selected.AsReadOnly();
        _selected.Add(seed);
        _anchor = seed;
    }

    internal SelectionScope(Action changed)
    {
        _selectedView = _selected.AsReadOnly();
        _changed = changed;
    }

    public IReadOnlyList<SelectionId> Selected => _selectedView;

    public SelectionId? Primary =>
        _selected.Count == 0 ? null : _selected[0];

    public SelectionId? Anchor => _anchor;

    public ActorId? PrimaryActor => Primary?.OwningActor;

    /// <summary>
    /// Optionally supplies a paired selection, such as a bone's left/right
    /// counterpart. Select and Add include a compatible partner before notifying
    /// listeners, without changing the clicked anchor. Remove removes both.
    /// Range selection and reconciliation do not add partners.
    /// </summary>
    public Func<SelectionId, SelectionId?>? CompanionResolver { get; set; }

    public bool IsSelected(SelectionId id) => _selected.Contains(id);

    public void Select(SelectionId id)
    {
        _selected.Clear();
        _selected.Add(id);
        _anchor = id;
        AddCompanion(id);
        NotifyChanged();
    }

    public void Add(SelectionId id)
    {
        if (_selected.Count > 0 && !IsCompatible(_selected[0], id))
        {
            Select(id);
            return;
        }

        if (!_selected.Contains(id))
            _selected.Add(id);
        _anchor = id;
        AddCompanion(id);
        NotifyChanged();
    }

    public void Toggle(SelectionId id)
    {
        if (IsSelected(id))
        {
            Remove(id);
            return;
        }

        Add(id);
    }

    public void Remove(SelectionId id)
    {
        if (!_selected.Remove(id))
            return;

        if (CompanionResolver?.Invoke(id) is { } companion && companion != id)
            _selected.Remove(companion);
        if (_anchor is not { } anchor || anchor == id || !_selected.Contains(anchor))
            _anchor = Primary;
        NotifyChanged();
    }

    /// <summary>
    /// Removes every selection with this actor's logical ID, including bones
    /// and bone groups, then sends one notification. Removing only the actor
    /// row would leave stale selections after actor destruction.
    /// </summary>
    public void RemoveActorLineage(Guid lineage)
    {
        int removed = _selected.RemoveAll(
            id => id.ActorLineage == lineage);
        if (removed == 0)
            return;
        if (_anchor is not { } anchor || !_selected.Contains(anchor))
            _anchor = Primary;
        NotifyChanged();
    }

    public void Promote(SelectionId id)
    {
        if (!_selected.Remove(id))
        {
            Select(id);
            return;
        }

        _selected.Insert(0, id);
        _anchor = id;
        NotifyChanged();
    }

    public void SelectRange(
        SelectionId from,
        SelectionId to,
        IReadOnlyList<SelectionId> displayOrder)
    {
        ArgumentNullException.ThrowIfNull(displayOrder);

        var fromIndex = IndexOf(displayOrder, from);
        var toIndex = IndexOf(displayOrder, to);

        // A hidden anchor or incompatible input replaces the selection with
        // the clicked target, matching the existing Ctrl-input contract.
        if (fromIndex < 0 || toIndex < 0 || !IsCompatible(to, from))
        {
            Select(to);
            return;
        }

        var start = Math.Min(fromIndex, toIndex);
        var end = Math.Max(fromIndex, toIndex);
        _selected.Clear();
        for (var index = start; index <= end; index++)
        {
            var candidate = displayOrder[index];
            // The clicked target anchors compatibility, so an incompatible
            // row inside the span cannot change the selected group.
            if (IsCompatible(to, candidate) && !_selected.Contains(candidate))
                _selected.Add(candidate);
        }

        _anchor = to;
        NotifyChanged();
    }

    public void Clear()
    {
        if (_selected.Count == 0 && _anchor == null)
            return;

        _selected.Clear();
        _anchor = null;
        NotifyChanged();
    }

    internal bool Reconcile(Func<SelectionId, SelectionId?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var next = new List<SelectionId>(_selected.Count);
        foreach (var candidate in _selected)
        {
            var resolved = resolver(candidate);
            if (resolved is { } value &&
                (next.Count == 0 || IsCompatible(next[0], value)) &&
                !next.Contains(value))
            {
                next.Add(value);
            }
        }

        var nextAnchor = _anchor is { } current ? resolver(current) : null;
        if (_selected.SequenceEqual(next) && _anchor == nextAnchor)
            return false;

        _selected.Clear();
        _selected.AddRange(next);
        _anchor = nextAnchor is { } kept && _selected.Contains(kept)
            ? kept
            : _selected.Count == 0 ? null : _selected[0];
        return true;
    }

    private void AddCompanion(SelectionId id)
    {
        if (CompanionResolver?.Invoke(id) is not { } companion ||
            companion == id ||
            _selected.Contains(companion) ||
            (_selected.Count > 0 && !IsCompatible(_selected[0], companion)))
            return;
        _selected.Add(companion);
    }

    private void NotifyChanged() => _changed?.Invoke();

    private static int IndexOf(
        IReadOnlyList<SelectionId> source,
        SelectionId value)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (source[index] == value)
                return index;
        }

        return -1;
    }

    private static bool IsCompatible(SelectionId left, SelectionId right)
    {
        // Bones may span skeleton slots but must belong to the same logical actor.
        // Scene entities can be selected together across kinds.
        if (left.Kind == SceneEntityKind.Bone
            || right.Kind == SceneEntityKind.Bone)
            return left.Kind == right.Kind
                && left.ActorLineage == right.ActorLineage;
        if (EntitySelection.IsEntity(left.Kind)
            && EntitySelection.IsEntity(right.Kind))
            return true;
        return left.Kind == right.Kind;
    }
}

/// <summary>Owns the live selection. Entity kinds may be mixed; selected bones
/// must belong to the same logical actor.</summary>
public sealed class SelectionSession
{
    private readonly SelectionScope _live;

    public SelectionSession()
    {
        _live = new SelectionScope(PublishLiveChanged);
    }

    public event Action<IReadOnlyList<SelectionId>>? SelectionChanged;

    /// <summary>The independently addressable live selection.</summary>
    public SelectionScope Live => _live;

    /// <summary>The live selection, for session-member callers.</summary>
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
