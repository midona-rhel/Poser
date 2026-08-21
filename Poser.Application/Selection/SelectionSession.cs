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

    /// <summary>
    /// A persistent companion for every selection — Ktisis' sibling link
    /// (SelectManager.cs:209-223), which resolves a bone's <c>_l</c>/<c>_r</c>
    /// counterpart so an edit, a reset or a flip covers both sides without a
    /// second click. Null (the default) is no companion at all.
    ///
    /// <para>Two properties this scope guarantees and the resolver must not
    /// have to think about: the companion joins BEFORE the change is
    /// published, so no listener ever observes the half-selection; and it
    /// never takes the ANCHOR, so a shift-range that follows still runs from
    /// the row the user actually clicked. Removal is symmetric — the pair
    /// leaves together, or the mode could strand a half-pair no click could
    /// have produced.</para>
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
    /// Drops EVERYTHING that belongs to one actor — the actor row, its bones,
    /// and its bone groups, which all carry the same
    /// <see cref="SelectionId.ActorLineage"/>.
    ///
    /// <para>It exists because a destroy path must never leave the selection
    /// pointing at something that no longer exists. Removing the actor row
    /// alone is not enough: a selected BONE outlives its actor just as
    /// happily, and every surface that reads the selection would then resolve
    /// a skeleton that has been freed. One notification for the whole lineage,
    /// so no listener observes a half-cleared actor.</para>
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
    /// restores nested adapters on disposal; legacy session-member writes to
    /// the redirected scope do not publish the live-selection event. Direct
    /// <see cref="Live"/> writes remain observable. Explicit scope callers do
    /// not use this method.
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
        if (ReferenceEquals(scope, _live))
        {
            throw new ArgumentException(
                "The live selection is reconciled automatically and cannot be tracked as a scope.",
                nameof(scope));
        }

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

        var liveChanged = _live.Reconcile(resolver);
        foreach (var scope in _scopes)
            scope.Reconcile(resolver);

        if (liveChanged)
            SelectionChanged?.Invoke(_live.Selected.ToArray());
    }

    private void PublishLiveChanged()
    {
        // Live is an explicit owner, so its notifications remain observable
        // even when legacy session members are redirected to a private scope.
        SelectionChanged?.Invoke(_live.Selected.ToArray());
    }
}
