using Poser.Domain.Identity;

namespace Poser.Application.Selection;

/// <summary>Stable-id selection authority with homogeneous grouping.</summary>
public sealed class SelectionSession
{
    private readonly List<SelectionId> _selected = new();

    public event Action<IReadOnlyList<SelectionId>>? SelectionChanged;

    public IReadOnlyList<SelectionId> Selected => _selected;
    public SelectionId? Primary => _selected.Count == 0 ? null : _selected[0];
    public SelectionId? Anchor { get; private set; }

    public bool IsSelected(SelectionId id) => _selected.Contains(id);

    public void Select(SelectionId id)
    {
        _selected.Clear();
        _selected.Add(id);
        Anchor = id;
        Publish();
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
        Anchor = id;
        Publish();
    }

    public void Toggle(SelectionId id)
    {
        if (_selected.Contains(id))
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
        if (Anchor == id)
            Anchor = Primary;
        Publish();
    }

    public void Promote(SelectionId id)
    {
        if (!_selected.Remove(id))
        {
            Select(id);
            return;
        }
        _selected.Insert(0, id);
        Anchor = id;
        Publish();
    }

    public void SelectRange(
        SelectionId from,
        SelectionId to,
        IReadOnlyList<SelectionId> displayOrder)
    {
        var fromIndex = IndexOf(displayOrder, from);
        var toIndex = IndexOf(displayOrder, to);
        if (fromIndex < 0 || toIndex < 0)
        {
            Select(from);
            Add(to);
            return;
        }

        var start = Math.Min(fromIndex, toIndex);
        var end = Math.Max(fromIndex, toIndex);
        _selected.Clear();
        for (var index = start; index <= end; index++)
        {
            var candidate = displayOrder[index];
            if (_selected.Count == 0 || IsCompatible(_selected[0], candidate))
                _selected.Add(candidate);
        }
        Anchor = to;
        Publish();
    }

    public void Clear()
    {
        if (_selected.Count == 0 && Anchor == null)
            return;
        _selected.Clear();
        Anchor = null;
        Publish();
    }

    internal void Reconcile(Func<SelectionId, SelectionId?> resolver)
    {
        var next = new List<SelectionId>(_selected.Count);
        foreach (var selected in _selected)
        {
            var resolved = resolver(selected);
            if (resolved is { } value &&
                (next.Count == 0 || IsCompatible(next[0], value)) &&
                !next.Contains(value))
                next.Add(value);
        }

        var nextAnchor = Anchor is { } anchor ? resolver(anchor) : null;
        if (_selected.SequenceEqual(next) && Anchor == nextAnchor)
            return;

        _selected.Clear();
        _selected.AddRange(next);
        Anchor = nextAnchor is { } candidate &&
                 _selected.Contains(candidate)
            ? candidate
            : Primary;
        Publish();
    }

    private static int IndexOf(
        IReadOnlyList<SelectionId> source,
        SelectionId value)
    {
        for (var index = 0; index < source.Count; index++)
            if (source[index] == value)
                return index;
        return -1;
    }

    private static bool IsCompatible(SelectionId left, SelectionId right)
    {
        if (left.Kind != right.Kind)
            return false;
        if (left.Kind == SceneEntityKind.Bone)
            return left.ActorLineage == right.ActorLineage;
        return true;
    }

    private void Publish() =>
        SelectionChanged?.Invoke(_selected.ToArray());
}
