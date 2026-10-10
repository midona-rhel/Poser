using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Game.Presentation;

/// <summary>Binds an exact stable ID to its live handle. A bound target keeps
/// the handle it was bound to; each write and replay resolves it again through
/// the history-only resolver, so a lifecycle restore that replaced the wrapper
/// still receives undo and redo. Public IDs are never repaired.</summary>
public sealed class HandleValuePort<TId, THandle> : IEntityValuePort<TId>
    where TId : struct where THandle : class
{
    private readonly Func<TId, THandle?> _resolveExact;
    private readonly Func<TId, SelectionId> _entity;
    private readonly Func<THandle, bool> _isValid;
    private readonly IEntityHistoryResolver<THandle>? _history;
    private readonly EntityAccessors<TId, THandle> _accessors;
    private readonly string _unavailable;

    public HandleValuePort(Func<TId, THandle?> resolveExact, Func<TId, SelectionId> entity,
        Func<THandle, bool> isValid, IEntityHistoryResolver<THandle>? history,
        EntityAccessors<TId, THandle> accessors, string unavailable)
    {
        _resolveExact = resolveExact;
        _entity = entity;
        _isValid = isValid;
        _history = history;
        _accessors = accessors;
        _unavailable = unavailable;
    }

    public IEntityValueTarget<TId>? Bind(TId id) =>
        _resolveExact(id) is { } handle ? new Target(this, handle, _entity(id)) : null;

    private THandle? Current(THandle original)
    {
        var current = _history is null ? original : _history.Resolve(original);
        return current is not null && _isValid(current) ? current : null;
    }

    private sealed class Target(HandleValuePort<TId, THandle> port, THandle original, SelectionId entity)
        : IEntityValueTarget<TId>
    {
        public SelectionId Entity => entity;

        public bool IsAlive => port.Current(original) is not null;

        public T Read<T>(EntityProperty<TId, T> property) =>
            port._accessors.Read(property, port.Current(original) ?? original);

        public ValueWriteResult Write<T>(EntityProperty<TId, T> property, T value) =>
            port.Current(original) is { } live
                ? port._accessors.Write(property, live, value)
                : new(false, port._unavailable);
    }
}
