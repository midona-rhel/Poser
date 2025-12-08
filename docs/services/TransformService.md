# TransformService

**Interface:** `ITransformService`
**Implementation:** `TransformService`
**Location:** `Poser/Services/TransformService.cs` (to be created)

## Purpose

Unified transform handling that dispatches to appropriate handlers based on entity type. This is the single entry point for all transform changes.

## Interface

```csharp
public interface ITransformService
{
    /// <summary>
    /// Apply a transform delta to an entity.
    /// Dispatches to appropriate handler based on entity type.
    /// </summary>
    void ApplyTransform(IEntity entity, Transform delta, TransformComponents components = TransformComponents.All);

    /// <summary>
    /// Set absolute transform for an entity.
    /// </summary>
    void SetTransform(IEntity entity, Transform transform);

    /// <summary>
    /// Get current transform of an entity.
    /// </summary>
    Transform GetTransform(IEntity entity);
}

[Flags]
public enum TransformComponents
{
    None = 0,
    Position = 1,
    Rotation = 2,
    Scale = 4,
    All = Position | Rotation | Scale
}
```

## Architecture

TransformService doesn't know how to transform anything itself. It delegates to type-specific handlers:

```
TransformService
    ├── IActorTransformHandler  → PosingService
    ├── IBoneTransformHandler   → BonePosingService
    └── ICategoryTransformHandler → Transforms all bones in category
```

## Events Published

| Event | When |
|-------|------|
| `TransformChangedEvent` | After any transform is applied |

## Events Subscribed

| Event | Action |
|-------|--------|
| `TransformChangeRequestedEvent` | Apply the requested transform |

## Implementation Pattern

```csharp
public class TransformService : ITransformService
{
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IEventBus _eventBus;

    public void ApplyTransform(IEntity entity, Transform delta, TransformComponents components)
    {
        var oldTransform = GetTransform(entity);

        switch (entity)
        {
            case IActor actor:
                ApplyToActor(actor, delta, components);
                break;

            case IBone bone:
                ApplyToBone(bone, delta, components);
                break;

            case ICategory category:
                // Apply same delta to all bones in category
                foreach (var bone in category.Bones)
                    ApplyToBone(bone, delta, components);
                break;

            default:
                throw new NotSupportedException($"Cannot transform {entity.GetType().Name}");
        }

        var newTransform = GetTransform(entity);
        _eventBus.Publish(new TransformChangedEvent(entity, oldTransform, newTransform));
    }

    private void ApplyToActor(IActor actor, Transform delta, TransformComponents components)
    {
        var current = _posingService.GetEffectiveTransform(actor);
        var newTransform = current.ApplyDelta(delta, components);
        _posingService.SetTransformOverride(actor, newTransform);
    }

    private void ApplyToBone(IBone bone, Transform delta, TransformComponents components)
    {
        _bonePosingService.ApplyTransform(bone, delta, null, components);
    }
}
```

## Multi-Selection Handling

When multiple entities are selected, the gizmo should call TransformService for each entity with the same delta. The HistoryService will collect all the TransformChangedEvents and create a composite action.

```csharp
// In GizmoOverlayWindow
foreach (var entity in _selectionService.Selected)
{
    _transformService.ApplyTransform(entity, delta);
}
// HistoryService collects all events into one CompositeAction
```

## Pivot and Orientation

The gizmo calculates the delta based on pivot/orientation settings. TransformService receives the final delta and doesn't need to know about pivot modes.

- **Pivot** affects where the gizmo is displayed and how rotation is calculated
- **Orientation** affects axis alignment
- **Delta** is what TransformService receives - already computed correctly
