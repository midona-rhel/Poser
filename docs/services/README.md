# Poser Services

This document provides an overview of all services in Poser.

## Service Categories

### Core Services

| Service | Interface | Purpose |
|---------|-----------|---------|
| [SelectionService](./SelectionService.md) | `ISelectionService` | Single source of truth for entity selection |
| [TransformService](./TransformService.md) | `ITransformService` | Applies transforms to entities, dispatches by type |
| [HistoryService](./HistoryService.md) | `IHistoryService` | Undo/redo stack with composite actions |
| [EventBus](./EventBus.md) | `IEventBus` | Publish/subscribe event system |

### Game Services

| Service | Interface | Purpose |
|---------|-----------|---------|
| [AnimationService](./AnimationService.md) | `IAnimationService` | Animation freeze, speed, scrubbing |
| [GazeService](./GazeService.md) | `IGazeService` | Gaze locking and targeting (eyes, head, body) |
| [SkeletonService](./SkeletonService.md) | `ISkeletonService` | Skeleton creation, bone management |
| [GPoseService](./GPoseService.md) | `IGPoseService` | GPose state detection |
| [CameraService](./CameraService.md) | `ICameraService` | Camera position/projection access |

### Entity Services

| Service | Interface | Purpose |
|---------|-----------|---------|
| [ActorManager](./ActorManager.md) | `IActorManager` | Actor lifecycle, refresh from game |
| [BonePosingService](./BonePosingService.md) | `IBonePosingService` | Bone transform hooks and application |
| [PosingService](./PosingService.md) | `IPosingService` | Actor transform overrides |

---

## Service Dependency Graph

```
                    ┌─────────────┐
                    │  EventBus   │
                    └──────┬──────┘
                           │ (all services publish/subscribe)
        ┌──────────────────┼──────────────────┐
        │                  │                  │
        ▼                  ▼                  ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│SelectionService│  │HistoryService │  │  GPoseService │
└───────┬───────┘  └───────┬───────┘  └───────┬───────┘
        │                  │                  │
        │                  │                  │
        ▼                  ▼                  ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│TransformService│  │AnimationService│  │ ActorManager │
└───────┬───────┘  └───────────────┘  └───────┬───────┘
        │                                      │
        ├──────────────────┬──────────────────┤
        ▼                  ▼                  ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│PosingService  │  │BonePosingService│ │SkeletonService│
│(Actor handler)│  │ (Bone handler) │  │               │
└───────────────┘  └───────────────┘  └───────────────┘
```

---

## Event Flow

### Transform Change Flow

```
1. User drags gizmo or edits property
   ↓
2. UI publishes TransformChangeRequestedEvent(entity, delta)
   ↓
3. TransformService handles:
   - Determines entity type
   - Calls appropriate handler
   - Publishes TransformChangedEvent(entity, old, new)
   ↓
4. HistoryService listens:
   - Collects changes during drag
   - Creates CompositeAction on drag end
   ↓
5. UI listens:
   - Updates display
```

### Selection Change Flow

```
1. User clicks entity in hierarchy/viewport
   ↓
2. UI calls SelectionService.Select(entity)
   ↓
3. SelectionService:
   - Updates internal state
   - Publishes SelectionChangedEvent
   ↓
4. PropertiesPanel listens:
   - Gets Primary selection
   - Checks capabilities (ITransformable, IAnimatable, etc.)
   - Shows appropriate editors
   ↓
5. Gizmo listens:
   - Updates target entities
```

---

## Service Initialization Order

Services are registered via dependency injection. Order matters for some:

1. **EventBus** - No dependencies, used by all
2. **GPoseService** - Game hooks, no service dependencies
3. **CameraService** - Game hooks, no service dependencies
4. **ActorManager** - Depends on GPoseService, EventBus
5. **SkeletonService** - Depends on ActorManager
6. **AnimationService** - Depends on GPoseService, EventBus
7. **GazeService** - Depends on GPoseService, CameraService, EventBus
8. **PosingService** - Depends on GPoseService, EventBus
9. **BonePosingService** - Depends on SkeletonService, EventBus
10. **SelectionService** - Depends on EventBus
11. **TransformService** - Depends on PosingService, BonePosingService, EventBus
12. **HistoryService** - Depends on EventBus

---

## Adding a New Service

1. Create interface in `Poser/Services/IYourService.cs`
2. Create implementation in `Poser/Game/YourService.cs` (if game-specific) or `Poser/Services/YourService.cs`
3. Register in `Poser.cs` ConfigureServices:
   ```csharp
   services.AddSingleton<IYourService, YourService>();
   ```
4. Document in `docs/services/YourService.md`
5. Add to this README
