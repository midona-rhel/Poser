namespace Poser.Application.Transforms;

/// <summary>Resolves an instance retained by history to its restored instance.
/// This is a history-only capability, never a way to repair public selection IDs.</summary>
public interface IEntityHistoryResolver<TEntity> where TEntity : class
{
    TEntity? Resolve(TEntity original);
}

/// <summary>Explicitly binds history to a replacement created by a replay.
/// This never repairs public selection IDs or pending operation receipts.</summary>
public interface IEntityHistoryBinding<TEntity> : IEntityHistoryResolver<TEntity> where TEntity : class
{
    void BindReplacement(TEntity original, TEntity replacement);
}
