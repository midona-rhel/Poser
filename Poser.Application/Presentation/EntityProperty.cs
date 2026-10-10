using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

/// <summary>Whether a property write becomes a journal step. Transport
/// (play, pause, speed) writes and reports but never journals.</summary>
public enum PropertyHistory { Journal, Transport }

/// <summary>One value an entity kind exposes, declared once per kind.</summary>
public abstract class EntityProperty(string key, PropertyHistory history)
{
    public string Key { get; } = key;
    public PropertyHistory History { get; } = history;
    public override string ToString() => Key;
}

public sealed class EntityProperty<TId, T>(string key, Func<T, string> describe,
    PropertyHistory history = PropertyHistory.Journal) : EntityProperty(key, history) where TId : struct
{
    public EntityProperty(string key, string description, PropertyHistory history = PropertyHistory.Journal)
        : this(key, _ => description, history) { }

    public string Describe(T value) => describe(value);
}

/// <summary>Binds a stable ID to its exact current entity, or null when stale.</summary>
public interface IEntityValuePort<TId> where TId : struct
{
    IEntityValueTarget<TId>? Bind(TId id);
}

/// <summary>One bound entity. Writes re-resolve it (history may follow a
/// restored replacement) and fail with detail when it has gone.</summary>
public interface IEntityValueTarget<TId> where TId : struct
{
    SelectionId Entity { get; }
    bool IsAlive { get; }
    T Read<T>(EntityProperty<TId, T> property);
    ValueWriteResult Write<T>(EntityProperty<TId, T> property, T value);
}

/// <summary>The one write path for an entity kind's declared properties:
/// bind the exact ID, apply the kind's refusal policy, then journal the
/// result-aware write under (entity, property) so continuous edits stage.</summary>
public sealed class EntityValues<TId>(ValueJournal journal, IEntityValuePort<TId> port, string unavailable,
    Func<IEntityValueTarget<TId>, EntityProperty, string?>? refuse = null) where TId : struct
{
    public ValueWriteResult Set<T>(TId id, EntityProperty<TId, T> property, T value) =>
        port.Bind(id) is { } target ? Write(target, property, value) : new(false, unavailable);

    /// <summary>Changes the value against the entity's current state, not a
    /// retained UI snapshot (one component of a vector, for example).</summary>
    public ValueWriteResult Update<T>(TId id, EntityProperty<TId, T> property, Func<T, T> change) =>
        port.Bind(id) is { } target ? Write(target, property, change(target.Read(property))) : new(false, unavailable);

    public void Seal() => journal.Seal();

    private ValueWriteResult Write<T>(IEntityValueTarget<TId> target, EntityProperty<TId, T> property, T value)
    {
        if (refuse?.Invoke(target, property) is { } why) return new(false, why);
        if (property.History == PropertyHistory.Transport)
        {
            try { return target.Write(property, value); }
            catch (Exception ex) { return new(false, ex.Message); }
        }
        return journal.Set((target.Entity, property.Key), property.Describe(value),
            () => target.Read(property), next => target.Write(property, next), value, () => target.IsAlive);
    }
}
