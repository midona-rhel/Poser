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
    private SelectionId? _anchor;
    private readonly Action? _changed;

    public SelectionScope(SelectionId seed)
    {
        _selected.Add(seed);
        _anchor = seed;
    }

    internal SelectionScope(Action changed)
    {
        _changed = changed;
    }

    public IReadOnlyList<SelectionId> Selected => _selected;

    public SelectionId? Primary =>
        _selected.Count == 0 ? null : _selected[0];

    public SelectionId? Anchor => _anchor;

    public bool IsSelected(SelectionId id) => _selected.Contains(id);

    public void Select(SelectionId id)
    {
        _selected.Clear();
        _selected.Add(id);
        _anchor = id;
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

        if (_anchor == id)
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
        if (left.Kind != right.Kind)
            return false;
        if (left.Kind == SceneEntityKind.Bone)
            return left.ActorLineage == right.ActorLineage;
        return true;
    }
}

/// <summary>Stable-id selection authority with homogeneous grouping.</summary>
public sealed class SelectionSession
{
    private readonly SelectionScope _live;
    private readonly List<SelectionScope> _scopes = new();

    // Compatibility-only cursor for current UI callers. New code should use
    // SelectionScope directly; UI migration will remove this adapter later.
    private SelectionScope? _compatibilityScope;

    public SelectionSession()
    {
        _live = new SelectionScope(PublishLiveChanged);
    }

    public event Action<IReadOnlyList<SelectionId>>? SelectionChanged;

    /// <summary>The independently addressable live selection.</summary>
    public SelectionScope Live => _live;

    /// <summary>
    /// The compatibility view used by legacy session-member callers while a
    /// <see cref="BeginScope"/> token is active.
    /// </summary>
    public IReadOnlyList<SelectionId> Selected => Target.Selected;

    public SelectionId? Primary => Target.Primary;

    public SelectionId? Anchor => Target.Anchor;

    /// <summary>
    /// Compatibility adapter for the current ambient UI callers. The token
    /// restores nested adapters on disposal; scoped writes do not publish the
    /// live-selection event. Explicit scope callers do not use this method.
    /// </summary>
    public IDisposable BeginScope(SelectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var previous = _compatibilityScope;
        _compatibilityScope = scope;
        return new CompatibilityScopeToken(this, previous);
    }

    /// <summary>
    /// Retains an explicit scope for stable-id reconciliation. The scope is
    /// still mutated directly; registration does not redirect session calls.
    /// </summary>
    public void TrackScope(SelectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!_scopes.Contains(scope))
            _scopes.Add(scope);
    }

    public void ForgetScope(SelectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scopes.Remove(scope);
    }

    private sealed class CompatibilityScopeToken(
        SelectionSession session,
        SelectionScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            session._compatibilityScope = previous;
        }
    }

    private SelectionScope Target => _compatibilityScope ?? _live;

    public bool IsSelected(SelectionId id) => Target.IsSelected(id);

    public void Select(SelectionId id) => Target.Select(id);

    public void Add(SelectionId id) => Target.Add(id);

    public void Toggle(SelectionId id) => Target.Toggle(id);

    public void Remove(SelectionId id) => Target.Remove(id);

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

        var liveChanged = _live.Reconcile(resolver);
        foreach (var scope in _scopes)
            scope.Reconcile(resolver);

        if (liveChanged)
            SelectionChanged?.Invoke(_live.Selected.ToArray());
    }

    private void PublishLiveChanged()
    {
        // Direct live operations publish unless a legacy adapter is active;
        // this preserves the old rule that scoped writes are private.
        if (_compatibilityScope == null)
            SelectionChanged?.Invoke(_live.Selected.ToArray());
    }
}
