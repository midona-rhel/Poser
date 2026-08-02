using System;
using System.Collections.Generic;

namespace Poser.UI.Reactive;

/// <summary>
/// Retained component identity. A scope is matched by (parent, component type,
/// key) and survives frames; the frame arena does not.
/// </summary>
internal sealed class ScopeTable
{
    internal sealed class Scope
    {
        internal Scope(int id, int parent, Type componentType, UiKey key, int frame)
        {
            Id = id;
            Parent = parent;
            ComponentType = componentType;
            Key = key;
            LastSeenFrame = frame;
        }

        internal int Id { get; }
        internal int Parent { get; }
        internal Type ComponentType { get; }
        internal UiKey Key { get; }
        internal object? Instance;
        internal object? State;
        internal object? PendingState;
        // Mount is proven by the FLAG, never by State being null: a component
        // whose state is legitimately null would otherwise re-mount forever.
        internal bool StateInitialized;
        // A QUEUED update is proven by its own flag for the same reason: a
        // reducer that returns null has still produced a new state, so the
        // promotion and the chaining read both key off this, never off
        // PendingState being non-null.
        internal bool HasPendingState;
        internal int LastSeenFrame;

        private Delegate?[] _reducers = new Delegate?[4];
        private int[] _reducerSlots = new int[4];
        private int _reducerCount;
        private int _reducerFrame = -1;

        /// <summary>
        /// Maps a reducer delegate to its arena object slot for the live frame.
        /// The delegate list is retained across frames, so a static lambda seen
        /// again costs a reference scan and nothing else.
        /// </summary>
        internal int ReducerSlot(Delegate reducer, FrameArena arena)
        {
            if (_reducerFrame != arena.FrameId)
            {
                Array.Clear(_reducerSlots, 0, _reducerCount);
                _reducerFrame = arena.FrameId;
            }

            for (int i = 0; i < _reducerCount; i++)
            {
                if (!ReferenceEquals(_reducers[i], reducer))
                    continue;
                if (_reducerSlots[i] == 0)
                    _reducerSlots[i] = arena.AddObject(reducer);
                return _reducerSlots[i];
            }

            if (_reducerCount == _reducers.Length)
            {
                Array.Resize(ref _reducers, _reducerCount * 2);
                Array.Resize(ref _reducerSlots, _reducerCount * 2);
            }

            _reducers[_reducerCount] = reducer;
            _reducerSlots[_reducerCount] = arena.AddObject(reducer);
            return _reducerSlots[_reducerCount++];
        }
    }

    private readonly record struct ScopeKey(int Parent, Type ComponentType, UiKey Key);

    private readonly Dictionary<ScopeKey, Scope> _scopes = [];
    private readonly Dictionary<int, Scope> _byId = [];
    private readonly List<ScopeKey> _removals = [];
    private int _nextId = 1;

    internal Scope GetOrCreate(int parentId, Type componentType, UiKey key, int frame)
    {
        ScopeKey lookup = new(parentId, componentType, key);
        if (_scopes.TryGetValue(lookup, out Scope? existing))
        {
#if DEBUG
            if (existing.LastSeenFrame == frame)
                throw new InvalidOperationException(
                    $"Duplicate sibling key '{key}' for {componentType.Name}: keys must be unique among siblings of one type.");
#endif
            existing.LastSeenFrame = frame;
            return existing;
        }

        Scope created = new(_nextId++, parentId, componentType, key, frame);
        _scopes.Add(lookup, created);
        _byId.Add(created.Id, created);
        return created;
    }

    internal Scope? Find(int id) => _byId.TryGetValue(id, out Scope? scope) ? scope : null;

    /// <summary>
    /// Unmount is only proven by a root that rendered to completion: a
    /// suspended or skipped root leaves its scopes alone.
    /// </summary>
    internal void CommitFrame(int frame, bool rootCompleted)
    {
        if (!rootCompleted)
            return;

        _removals.Clear();
        foreach (KeyValuePair<ScopeKey, Scope> entry in _scopes)
        {
            if (entry.Value.LastSeenFrame < frame)
                _removals.Add(entry.Key);
        }

        for (int i = 0; i < _removals.Count; i++)
        {
            if (_scopes.Remove(_removals[i], out Scope? removed))
                _byId.Remove(removed.Id);
        }
    }
}
