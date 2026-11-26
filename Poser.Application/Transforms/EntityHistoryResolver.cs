namespace Poser.Application.Transforms;

/// <summary>Finds the thing that undo brought back in place of one that was
/// deleted, so older undo steps act on it. Only undo and redo use this; a
/// deleted thing's id stays invalid everywhere else.</summary>
public interface IEntityHistoryResolver<TEntity> where TEntity : class
{
    TEntity? Resolve(TEntity original);
}

/// <summary>Records which thing undo or redo made in place of a deleted one.
/// Only undo and redo read the link.</summary>
public interface IEntityHistoryBinding<TEntity> : IEntityHistoryResolver<TEntity> where TEntity : class
{
    void BindReplacement(TEntity original, TEntity replacement);
}
