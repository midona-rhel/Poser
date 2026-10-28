using System.Runtime.CompilerServices;
using Poser.Application.Scene;
using Poser.Domain.Operations;
using Poser.Domain.Identity;

namespace Poser.Game.Scene;

/// <summary>Keeps native entities behind session-scoped weak handles.</summary>
internal sealed class SceneRuntimeHandles(Func<SessionGeneration?> activeSession)
{
    private sealed class Entry(object entity)
    {
        public object Entity { get; } = entity;
        public bool Removed { get; set; }
    }

    private readonly ConditionalWeakTable<SceneEntityHandle, Entry> _entities = new();
    private SessionGeneration? _session;

    public void Synchronize()
    {
        var current = activeSession();
        if (current == _session) return;
        _entities.Clear();
        _session = current;
    }

    public SceneEntityHandle Track(SceneEntityKind kind, object entity)
    {
        Synchronize();
        var session = _session ?? throw new InvalidOperationException("No active scene session.");
        var handle = new SceneEntityHandle(session, kind);
        _entities.Add(handle, new(entity));
        return handle;
    }

    public object? Resolve(SceneEntityHandle? handle)
    {
        Synchronize();
        return handle != null && handle.Session == _session
            && _entities.TryGetValue(handle, out var entry) && !entry.Removed ? entry.Entity : null;
    }

    public object? ResolveHistory(SceneEntityHandle handle)
    {
        Synchronize();
        return handle.Session == _session && _entities.TryGetValue(handle, out var entry)
            ? entry.Entity : null;
    }

    public T? Resolve<T>(SceneEntityHandle? handle, SceneEntityKind kind) where T : class =>
        handle?.Kind == kind ? Resolve(handle) as T : null;

    public T Require<T>(SceneEntityHandle handle, SceneEntityKind kind) where T : class =>
        Resolve<T>(handle, kind) ?? throw new InvalidOperationException(
            $"The scene {kind} is no longer available in this runtime/session.");

    public void Forget(SceneEntityHandle handle) => _entities.Remove(handle);

    public void Remove<T>(SceneEntityHandle handle, SceneEntityKind kind,
        Func<T, T?> resolveHistory, Action<T> remove) where T : class
    {
        // Removal follows a history replacement when one exists. Other handle
        // reads continue to refer to the original runtime instance.
        if (Resolve<T>(handle, kind) is not { } original) return;
        if (resolveHistory(original) is { } current) remove(current);
        // History can retain the weak handle for redo, but normal reads must
        // no longer resolve the removed entity.
        if (_entities.TryGetValue(handle, out var entry)) entry.Removed = true;
    }

    public void Clear() => _entities.Clear();
}
