namespace Poser.Application.Transforms;

/// <summary>Finds the thing that undo or redo brought back in place of one that
/// was deleted, so older undo steps act on the thing that is there now.</summary>
public interface IEntityHistoryResolver<TEntity> where TEntity : class
{
    TEntity? Resolve(TEntity original);
}

/// <summary>Records which new thing stands in for an earlier one, so the undo
/// steps recorded for the earlier one act on the new one.</summary>
public interface IEntityHistoryBinding<TEntity> : IEntityHistoryResolver<TEntity> where TEntity : class
{
    void BindReplacement(TEntity original, TEntity replacement);
}
