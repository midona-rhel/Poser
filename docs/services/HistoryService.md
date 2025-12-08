# HistoryService

**Interface:** `IHistoryService`
**Implementation:** `HistoryService`
**Location:** `Poser/History/HistoryService.cs`

## Purpose

Manages undo/redo stack for all reversible actions. Supports composite actions for multi-entity operations.

## Interface

```csharp
public interface IHistoryService
{
    bool CanUndo { get; }
    bool CanRedo { get; }

    void Push(IHistoryAction action);
    void Record(IHistoryAction action);  // Alias for Push
    void Undo();
    void Redo();
    void Clear();
}
```

## History Actions

All undoable operations implement `IHistoryAction`:

```csharp
public interface IHistoryAction
{
    string Description { get; }
    void Execute();
    void Undo();
}
```

### Existing Action Types

| Action | Description |
|--------|-------------|
| `TransformHistoryAction` | Actor transform change |
| `TransformBoneAction` | Bone transform change |
| `TransformActorAction` | Actor transform change |
| `SpeedChangeAction` | Animation speed change |
| `BaseAnimationAction` | Base animation change |
| `GazeHistoryAction` | Gaze state change |
| `CompositeAction` | Multiple actions as one |

## Composite Actions

When multiple entities are transformed together, use `CompositeAction`:

```csharp
public class CompositeAction : IHistoryAction
{
    public string Description { get; }
    private readonly List<IHistoryAction> _actions;

    public CompositeAction(string description, IEnumerable<IHistoryAction> actions)
    {
        Description = description;
        _actions = actions.ToList();
    }

    public void Execute()
    {
        foreach (var action in _actions)
            action.Execute();
    }

    public void Undo()
    {
        // Undo in reverse order
        for (int i = _actions.Count - 1; i >= 0; i--)
            _actions[i].Undo();
    }
}
```

### Usage for Multi-Selection

```csharp
// When gizmo drag ends with multiple selected entities
var actions = new List<IHistoryAction>();
foreach (var (entity, startTransform) in _dragStartTransforms)
{
    var endTransform = _transformService.GetTransform(entity);
    if (startTransform != endTransform)
    {
        actions.Add(new TransformAction(entity, startTransform, endTransform));
    }
}

if (actions.Count == 1)
    _historyService.Push(actions[0]);
else if (actions.Count > 1)
    _historyService.Push(new CompositeAction($"Transform {actions.Count} entities", actions));
```

## Event-Based Recording (Proposed)

Instead of UI creating actions directly, HistoryService could listen to events:

```csharp
// During drag operation
_eventBus.Publish(new TransformDragStartedEvent(entities));

// On each change
_eventBus.Publish(new TransformChangedEvent(entity, old, new));

// When drag ends
_eventBus.Publish(new TransformDragEndedEvent());

// HistoryService collects events between Start/End into CompositeAction
```

## Events Subscribed (Proposed)

| Event | Action |
|-------|--------|
| `TransformDragStartedEvent` | Start collecting changes |
| `TransformChangedEvent` | Record change |
| `TransformDragEndedEvent` | Create composite action |
| `GPoseStateChangedEvent` | Clear history on GPose exit |

## Stack Behavior

```
[Initial State]
    ↓ Push(Action A)
[A] ← Current
    ↓ Push(Action B)
[A, B] ← Current
    ↓ Undo()
[A] ← Current, [B] in redo stack
    ↓ Push(Action C)
[A, C] ← Current, redo stack cleared
```
