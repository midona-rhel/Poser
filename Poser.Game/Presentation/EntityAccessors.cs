using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;

namespace Poser.Game.Presentation;

/// <summary>How one entity kind's declared properties read and write a live
/// handle. <see cref="Complete"/> fails at construction when a declared
/// property has no accessor, so a new property cannot ship unwired.</summary>
public sealed class EntityAccessors<TId, THandle>(string unchanged) where TId : struct
{
    private readonly Dictionary<EntityProperty, object> _accessors = new();

    public EntityAccessors<TId, THandle> Map<T>(EntityProperty<TId, T> property,
        Func<THandle, T> read, Func<THandle, T, Outcome> write)
    {
        _accessors.Add(property, new Accessor<T>(read, write));
        return this;
    }

    /// <summary>A plain setter. <paramref name="verify"/> reads the value back
    /// for setters that silently ignore a write they cannot apply.</summary>
    public EntityAccessors<TId, THandle> Assign<T>(EntityProperty<TId, T> property,
        Func<THandle, T> read, Action<THandle, T> assign, bool verify = false) =>
        Map(property, read, (handle, value) =>
        {
            assign(handle, value);
            return !verify || EqualityComparer<T>.Default.Equals(read(handle), value)
                ? Outcome.Ok() : new(false, unchanged);
        });

    public EntityAccessors<TId, THandle> Complete(IReadOnlyList<EntityProperty> declared)
    {
        var missing = declared.Where(property => !_accessors.ContainsKey(property))
            .Select(property => property.Key).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"No accessor for {string.Join(", ", missing)}.");
        return this;
    }

    public T Read<T>(EntityProperty<TId, T> property, THandle handle) => Get(property).Read(handle);

    public Outcome Write<T>(EntityProperty<TId, T> property, THandle handle, T value) =>
        Get(property).Write(handle, value);

    private Accessor<T> Get<T>(EntityProperty<TId, T> property) =>
        _accessors.TryGetValue(property, out var accessor) ? (Accessor<T>)accessor
            : throw new InvalidOperationException($"No accessor for {property.Key}.");

    private sealed record Accessor<T>(Func<THandle, T> Read, Func<THandle, T, Outcome> Write);
}
