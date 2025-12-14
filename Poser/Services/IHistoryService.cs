using System;

namespace Poser.Services;

/// <summary>
/// Represents an action that can be undone and redone.
/// </summary>
public interface IHistoryAction
{
    string Description { get; }
    void Execute();
    void Undo();
}

/// <summary>
/// Provides undo/redo functionality.
/// </summary>
public interface IHistoryService : IDisposable
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? UndoDescription { get; }
    string? RedoDescription { get; }

    /// <summary>
    /// Push an action and execute it.
    /// </summary>
    void Push(IHistoryAction action);

    /// <summary>
    /// Record an already-executed action without re-executing it.
    /// Use when the action was already applied (e.g., during slider drag).
    /// </summary>
    void Record(IHistoryAction action);

    void Undo();
    void Redo();
    void Clear();

    // History changes are published via EventBus: HistoryChangedEvent
}
