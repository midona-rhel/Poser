# SelectionService

**Interface:** `ISelectionService`
**Implementation:** `SelectionService`
**Location:** `Poser/Services/SelectionService.cs` (to be created)

## Purpose

Single source of truth for entity selection. Replaces the dual selection management that existed in ActorManager and EditorState.

## Interface

```csharp
public interface ISelectionService
{
    // State
    IReadOnlyList<IEntity> Selected { get; }
    IEntity? Primary { get; }  // First selected entity

    // Actions
    void Select(IEntity entity);              // Clear and select single
    void AddToSelection(IEntity entity);      // Add to existing (Ctrl+click)
    void RemoveFromSelection(IEntity entity); // Remove from selection
    void ToggleSelection(IEntity entity);     // Toggle selection state
    void SelectRange(IEntity from, IEntity to); // Range select (Shift+click)
    void ClearSelection();

    // Queries
    bool IsSelected(IEntity entity);
    IEnumerable<T> GetSelected<T>() where T : IEntity;
}
```

## Events Published

| Event | When |
|-------|------|
| `SelectionChangedEvent` | Any selection change |

## Events Subscribed

| Event | Action |
|-------|--------|
| `GPoseStateChangedEvent` | Clear selection on GPose exit |
| `EntityRemovedEvent` | Remove from selection if selected |

## Usage Examples

### Single Selection
```csharp
_selectionService.Select(actor);
// Clears previous selection, selects actor
// Publishes SelectionChangedEvent
```

### Multi-Selection
```csharp
_selectionService.AddToSelection(bone1);
_selectionService.AddToSelection(bone2);
// bone1 and bone2 both selected
// Primary is bone1 (first added)
```

### Query by Type
```csharp
var selectedBones = _selectionService.GetSelected<IBone>().ToList();
var selectedActors = _selectionService.GetSelected<IActor>().ToList();
```

## Integration with Properties Panel

The Properties Panel reads from `SelectionService.Primary` and checks capabilities:

```csharp
var primary = _selectionService.Primary;
if (primary == null) return;

if (primary is ITransformable transformable)
    DrawTransformEditor(transformable);

if (primary is IAnimatable animatable)
    DrawAnimationEditor(animatable);

if (primary is IGazeable gazeable)
    DrawGazeEditor(gazeable);
```

## Migration Notes

Currently selection is split between:
- `EditorState._selectedEntities` - General entity selection
- `ActorManager.SelectedActors` - Actor-specific selection
- `EditorState._selectedCategory` - Category selection

All should be unified into SelectionService, with Category becoming an IEntity.
