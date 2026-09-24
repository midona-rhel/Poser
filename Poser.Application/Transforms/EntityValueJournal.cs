namespace Poser.Application.Transforms;

/// <summary>Resolves an instance retained by history to its restored instance.
/// This is a history-only capability, never a way to repair public selection IDs.</summary>
public interface IEntityHistoryResolver<TEntity> where TEntity : class
{
    TEntity? Resolve(TEntity original);
}

/// <summary>Property history resolves its entity again at replay time, so
/// lifecycle restoration can replace a runtime wrapper without losing edits.</summary>
public sealed class EntityValueJournal<TEntity>(
    ValueJournal journal,
    Func<TEntity, bool> isValid,
    IEntityHistoryResolver<TEntity>? resolver = null) where TEntity : class
{
    public TEntity? Current(TEntity original)
    {
        var current = resolver is null ? original : resolver.Resolve(original);
        return current is not null && isValid(current) ? current : null;
    }

    public void Set<T>(TEntity original, string property, string description,
        Func<TEntity, T> read, Action<TEntity, T> write, T value)
    {
        if (Current(original) is not { } current) return;
        journal.Set((original, property), description, () => read(current),
            next => { if (Current(original) is { } live) write(live, next); },
            value, () => Current(original) is not null);
    }
}
