using Poser.Application.Transforms;
using Poser.Domain;
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
    Outcome Write<T>(EntityProperty<TId, T> property, T value);
}

/// <summary>The one way to change an entity kind's declared properties with
/// undo. <see cref="Set{T}"/> and <see cref="Update{T}"/> find the entity by
/// its id, refuse when the kind's rule says so (a locked camera, for example),
/// write the value and record one undo step per entity and property, so a
/// slider drag becomes one step. <see cref="Record{T}"/> only records a change
/// the game already made.</summary>
public sealed class EntityValues<TId>(ValueJournal journal, IEntityValuePort<TId> port, string unavailable,
    Func<IEntityValueTarget<TId>, EntityProperty, string?>? refuse = null) where TId : struct
{
    public Outcome Set<T>(TId id, EntityProperty<TId, T> property, T value) =>
        port.Bind(id) is { } target ? Write(target, property, value) : new(false, unavailable);

    /// <summary>Changes the value against the entity's current state, not a
    /// retained UI snapshot (one component of a vector, for example).</summary>
    public Outcome Update<T>(TId id, EntityProperty<TId, T> property, Func<T, T> change) =>
        port.Bind(id) is { } target ? Write(target, property, change(target.Read(property))) : new(false, unavailable);

    /// <summary>Records a change the game already made, such as an overlay
    /// the player dragged on screen, as one undo step from
    /// <paramref name="before"/> to the entity's current value. Undo and redo
    /// go through the same target as <see cref="Set{T}"/>, so they follow an
    /// entity that was removed and brought back by undo.</summary>
    public void Record<T>(TId id, EntityProperty<TId, T> property, T before)
    {
        if (port.Bind(id) is not { } target)
            return;
        var after = target.Read(property);
        journal.Record((target.Entity, property.Key), property.Describe(after), before, after,
            next => target.Write(property, next), () => target.IsAlive);
    }

    public void Seal() => journal.Seal();

    private Outcome Write<T>(IEntityValueTarget<TId> target, EntityProperty<TId, T> property, T value)
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
