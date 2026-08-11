using Poser.Domain.Identity;

namespace Poser.Application.Selection;

/// <summary>
/// A window's own selection state, substituted into the session for the span
/// of that window's draw. A frozen pop-out seeds one with its entity and
/// wraps its frame in <see cref="SelectionSession.BeginScope"/>: every pane
/// and facade it hosts then resolves — and edits — the scope's selection,
/// while the live selection (the gizmo's, the sidebar's) stays untouched.
/// Registered scopes ride the same stable-id reconciliation the live list
/// gets, so a scope survives scene refreshes exactly as far as its ids do.
/// </summary>
public sealed class SelectionScope
{
    internal readonly List<SelectionId> Selected = new();
    internal SelectionId? Anchor;

    public SelectionScope(SelectionId seed)
    {
        Selected.Add(seed);
        Anchor = seed;
    }

    public SelectionId? Primary =>
        Selected.Count == 0 ? null : Selected[0];
}

/// <summary>Stable-id selection authority with homogeneous grouping.</summary>
public sealed class SelectionSession
{
    private readonly List<SelectionId> _liveSelected = new();
    private SelectionId? _liveAnchor;

    /// <summary>The scope writes and reads land on while one is active —
    /// active only inside a pop-out window's draw, on the one UI thread.
    /// </summary>
    private SelectionScope? _scope;

    /// <summary>Scopes registered for reconciliation alongside the live list.
    /// </summary>
    private readonly List<SelectionScope> _scopes = new();

    public event Action<IReadOnlyList<SelectionId>>? SelectionChanged;

    public IReadOnlyList<SelectionId> Selected =>
        _scope?.Selected ?? _liveSelected;
    public SelectionId? Primary
    {
        get
        {
            var selected = _scope?.Selected ?? _liveSelected;
            return selected.Count == 0 ? null : selected[0];
        }
    }
    public SelectionId? Anchor
    {
        get => _scope is { } scope ? scope.Anchor : _liveAnchor;
        private set
        {
            if (_scope is { } scope)
                scope.Anchor = value;
            else
                _liveAnchor = value;
        }
    }

    /// <summary>Substitutes <paramref name="scope"/> for the live selection
    /// until the returned token is disposed. Scoped writes do not publish —
    /// <see cref="SelectionChanged"/> subscribers are live-selection
    /// listeners.</summary>
    public IDisposable BeginScope(SelectionScope scope)
    {
        var previous = _scope;
        _scope = scope;
        return new ScopeToken(this, previous);
    }

    /// <summary>Keeps <paramref name="scope"/> id-fresh across scene
    /// refreshes; forgotten with <see cref="ForgetScope"/> when its window
    /// closes.</summary>
    public void TrackScope(SelectionScope scope) => _scopes.Add(scope);

    public void ForgetScope(SelectionScope scope) => _scopes.Remove(scope);

    private sealed class ScopeToken(
        SelectionSession session, SelectionScope? previous) : IDisposable
    {
        public void Dispose() => session._scope = previous;
    }

    /// <summary>The list a member operates on this call: the active scope's,
    /// else the live one.</summary>
    private List<SelectionId> Target => _scope?.Selected ?? _liveSelected;

    public bool IsSelected(SelectionId id) => Target.Contains(id);

    public void Select(SelectionId id)
    {
        var target = Target;
        target.Clear();
        target.Add(id);
        Anchor = id;
        Publish();
    }

    public void Add(SelectionId id)
    {
        var target = Target;
        if (target.Count > 0 && !IsCompatible(target[0], id))
        {
            Select(id);
            return;
        }

        if (!target.Contains(id))
            target.Add(id);
        Anchor = id;
        Publish();
    }

    public void Toggle(SelectionId id)
    {
        if (Target.Contains(id))
        {
            Remove(id);
            return;
        }
        Add(id);
    }

    public void Remove(SelectionId id)
    {
        if (!Target.Remove(id))
            return;
        if (Anchor == id)
            Anchor = Primary;
        Publish();
    }

    public void Promote(SelectionId id)
    {
        var target = Target;
        if (!target.Remove(id))
        {
            Select(id);
            return;
        }
        target.Insert(0, id);
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

        // No visible range (the anchor is filtered or collapsed away) or an
        // incompatible anchor: the clicked target replaces the selection —
        // the same contract as incompatible Ctrl input.
        if (fromIndex < 0 || toIndex < 0 || !IsCompatible(to, from))
        {
            Select(to);
            return;
        }

        var start = Math.Min(fromIndex, toIndex);
        var end = Math.Max(fromIndex, toIndex);
        var target = Target;
        target.Clear();
        for (var index = start; index <= end; index++)
        {
            var candidate = displayOrder[index];
            // Compatibility is anchored on the CLICKED target so an
            // incompatible row inside the span can never redefine the group
            // or exclude the clicked target itself.
            if (IsCompatible(to, candidate) && !target.Contains(candidate))
                target.Add(candidate);
        }
        Anchor = to;
        Publish();
    }

    public void Clear()
    {
        if (Target.Count == 0 && Anchor == null)
            return;
        Target.Clear();
        Anchor = null;
        Publish();
    }

    internal void Reconcile(Func<SelectionId, SelectionId?> resolver)
    {
        bool changed = ReconcileList(
            _liveSelected, ref _liveAnchor, resolver);
        foreach (var scope in _scopes)
        {
            var anchor = scope.Anchor;
            ReconcileList(scope.Selected, ref anchor, resolver);
            scope.Anchor = anchor;
        }
        if (changed)
            SelectionChanged?.Invoke(_liveSelected.ToArray());
    }

    private static bool ReconcileList(
        List<SelectionId> selected,
        ref SelectionId? anchor,
        Func<SelectionId, SelectionId?> resolver)
    {
        var next = new List<SelectionId>(selected.Count);
        foreach (var candidate in selected)
        {
            var resolved = resolver(candidate);
            if (resolved is { } value &&
                (next.Count == 0 || IsCompatible(next[0], value)) &&
                !next.Contains(value))
                next.Add(value);
        }

        var nextAnchor = anchor is { } current ? resolver(current) : null;
        if (selected.SequenceEqual(next) && anchor == nextAnchor)
            return false;

        selected.Clear();
        selected.AddRange(next);
        anchor = nextAnchor is { } kept && selected.Contains(kept)
            ? kept
            : selected.Count == 0 ? null : selected[0];
        return true;
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

    private void Publish()
    {
        // A scoped write is the pop-out window's own affair; subscribers —
        // gesture cancellation, the overlay — watch the live selection.
        if (_scope != null)
            return;
        SelectionChanged?.Invoke(_liveSelected.ToArray());
    }
}
