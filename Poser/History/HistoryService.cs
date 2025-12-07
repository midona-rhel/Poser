using System;
using System.Collections.Generic;
using Poser.Core;
using Poser.Services;

namespace Poser.History;

public class HistoryService : IHistoryService, IDisposable
{
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly Stack<IHistoryAction> _undoStack = new();
    private readonly Stack<IHistoryAction> _redoStack = new();

    public HistoryService(IGPoseService gPoseService, IEventBus eventBus)
    {
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            // Clear history when exiting GPose
            Clear();
        }
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;
    public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

    public event Action? OnHistoryChanged;

    public void Push(IHistoryAction action)
    {
        // Execute the action
        action.Execute();

        // Add to undo stack
        _undoStack.Push(action);

        // Clear redo stack (new action invalidates redo history)
        _redoStack.Clear();

        OnHistoryChanged?.Invoke();
    }

    public void Record(IHistoryAction action)
    {
        // Add to undo stack WITHOUT executing (action was already applied)
        _undoStack.Push(action);

        // Clear redo stack (new action invalidates redo history)
        _redoStack.Clear();

        OnHistoryChanged?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;

        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);

        OnHistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;

        var action = _redoStack.Pop();
        action.Execute();
        _undoStack.Push(action);

        OnHistoryChanged?.Invoke();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        OnHistoryChanged?.Invoke();
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        GC.SuppressFinalize(this);
    }
}
