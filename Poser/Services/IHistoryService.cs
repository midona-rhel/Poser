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
public interface IHistoryService
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? UndoDescription { get; }
    string? RedoDescription { get; }

    void Push(IHistoryAction action);
    void Undo();
    void Redo();
    void Clear();

    event Action? OnHistoryChanged;
}
