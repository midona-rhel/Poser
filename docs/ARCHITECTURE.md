# Poser Architecture

## Vision

A clean, library-extractable posing system for FFXIV. The core posing logic should be separable from the Dalamud plugin UI.

---

## Core Principles

1. **Standard Dependency Injection** - UI components inject the services they need
2. **Direct Method Calls** - UI reads state from services, calls methods on services
3. **Events for Cross-Cutting Concerns** - Only system state changes and history recording
4. **Single Source of Truth** - One service owns each piece of state
5. **Interfaces for Testability** - All services behind interfaces

---

## Architecture Pattern

### UI → Service (Direct Calls)

```
UI Component
    ↓ inject
Service Interface
    ↓ implement
Service Implementation → Game Hooks
```

**Example:**
```csharp
public class EntityList
{
    private readonly ISelectionService _selectionService;
    private readonly IAnimationService _animationService;

    public void Draw()
    {
        // Read state directly from services
        bool isSelected = _selectionService.IsSelected(entity);
        bool isFrozen = _animationService.IsFrozen(actor);

        // Call methods directly on services
        if (clicked)
            _selectionService.Select(entity);
        if (freezeToggled)
            _animationService.ToggleFreeze(actor);
    }
}
```

### Events (Cross-Cutting Only)

Events are **only** used for:

1. **System state changes** - GPose enter/exit, posing mode changes
2. **History recording** - Transform drag start/end for undo/redo
3. **Notification** - Selection changed (for skeleton overlay to update)

**Not used for:**
- UI-to-service communication (use direct method calls)
- State synchronization (read from services)

```csharp
// System events
public record GPoseStateChangedEvent(bool IsGPosing);
public record PosingModeChangedEvent(bool IsPosingMode);
public record ActorListChangedEvent(IReadOnlyList<IActor> Actors);

// History events
public record TransformDragStartedEvent(IReadOnlyList<IEntity> Entities);
public record TransformDragEndedEvent();

// Notification events
public record SelectionChangedEvent(IReadOnlyList<IEntity> Selected);
```

---

## Project Structure

```
Poser/
├── Core/               # Core types and event bus
│   ├── EventBus.cs     # Simple pub/sub
│   ├── Events.cs       # Event definitions
│   ├── EditorState.cs  # Editor settings (tool, pivot, orientation)
│   └── Transform.cs    # Transform math
│
├── Entities/           # Entity definitions
│   ├── IEntity.cs      # Base entity interface
│   ├── IActor.cs       # Actor interface
│   ├── IBone.cs        # Bone interface
│   ├── ISkeleton.cs    # Skeleton interface
│   ├── Capabilities/   # Capability interfaces (ITransformable, etc.)
│   └── ...             # Implementations
│
├── Services/           # Service interfaces
│   ├── ISelectionService.cs
│   ├── IAnimationService.cs
│   ├── IGazeService.cs
│   ├── IPosingService.cs
│   ├── IBonePosingService.cs
│   ├── IHistoryService.cs
│   └── ...
│
├── Game/               # Service implementations with game hooks
│   ├── SelectionService.cs
│   ├── AnimationService.cs
│   ├── GazeService.cs
│   └── ...
│
├── History/            # Undo/redo system
│   ├── HistoryService.cs
│   └── TransformHistoryAction.cs
│
└── UI/                 # ImGui interface
    ├── MainWindow.cs
    ├── GizmoOverlayWindow.cs
    ├── SkeletonOverlayWindow.cs
    └── Components/
        ├── TopBar.cs
        ├── ScenePanel.cs
        ├── EntityList.cs
        └── PropertiesPanel.cs
```

---

## Service Responsibilities

### ISelectionService
- **Single source of truth** for what's selected
- Methods: `Select()`, `AddToSelection()`, `ToggleSelection()`, `ClearSelection()`
- State: `Selected`, `Primary`, `IsSelected(entity)`
- Publishes: `SelectionChangedEvent`

### IAnimationService
- Controls animation playback
- Methods: `Freeze()`, `Unfreeze()`, `SetSpeed()`, `ResetSpeed()`
- State: `IsFrozen(actor)`, `GetSpeed(actor)`

### IGazeService
- Controls head/eye tracking
- Methods: `SetGazeMode()`, `SetGazeTarget()`, `LockGaze()`, `UnlockGaze()`
- State: `GetGazeState(actor)`

### IPosingService
- Actor transform manipulation
- Methods: `SetTransformOverride()`, `ClearOverride()`
- State: `GetEffectiveTransform(actor)`

### IBonePosingService
- Bone transform manipulation
- Methods: `ApplyTransform()`, `ResetBone()`, `ResetSkeleton()`
- State: `GetModification(bone)`, `HasModifications(bone)`

### IHistoryService
- Undo/redo management
- Subscribes to: `TransformDragStartedEvent`, `TransformDragEndedEvent`
- Auto-records transform changes from gizmo drags
- Methods: `Undo()`, `Redo()`, `Record(action)`

### IEditorState
- Editor tool settings
- State: `TransformTool`, `TransformPivot`, `TransformOrientation`
- State: `IsPosingMode`, `DebugMode`

---

## UI Component Pattern

Each UI component:
1. **Injects** the services it needs via constructor
2. **Reads** state directly from services during Draw()
3. **Calls** service methods when user interacts
4. **Does not** cache state from events (read fresh each frame)

```csharp
public class PropertiesPanel
{
    private readonly ISelectionService _selectionService;
    private readonly IAnimationService _animationService;

    public PropertiesPanel(ISelectionService selectionService, IAnimationService animationService)
    {
        _selectionService = selectionService;
        _animationService = animationService;
    }

    public void Draw()
    {
        var actor = _selectionService.GetFirstSelected<IActor>();
        if (actor == null) return;

        // Read state fresh each frame
        bool isFrozen = _animationService.IsFrozen(actor);
        float speed = _animationService.GetSpeed(actor);

        // Render UI
        if (ImGui.Checkbox("Frozen", ref isFrozen))
            _animationService.ToggleFreeze(actor);
    }
}
```

---

## History System

The HistoryService auto-records transform changes:

1. **GizmoOverlayWindow** publishes `TransformDragStartedEvent` when drag begins
2. **HistoryService** captures initial transforms
3. **GizmoOverlayWindow** publishes `TransformDragEndedEvent` when drag ends
4. **HistoryService** compares, creates action, adds to undo stack

```csharp
public class HistoryService
{
    public HistoryService(IEventBus eventBus, ...)
    {
        eventBus.Subscribe<TransformDragStartedEvent>(OnDragStarted);
        eventBus.Subscribe<TransformDragEndedEvent>(OnDragEnded);
    }

    private void OnDragStarted(TransformDragStartedEvent e)
    {
        // Capture initial transforms
        _dragStartTransforms = CaptureTransforms(e.Entities);
    }

    private void OnDragEnded(TransformDragEndedEvent e)
    {
        // Capture final transforms, create action, record
        var endTransforms = CaptureTransforms(_dragEntities);
        Record(new TransformHistoryAction(...));
    }
}
```

---

## Capability Interfaces

Capability interfaces exist in `Entities/Capabilities/`:
- `ITransformable` - Can be positioned in 3D space
- `IAnimatable` - Can be animated (freeze, speed)
- `IGazeable` - Has gaze control (eyes, head, body)
- `ISkeletonOwner` - Has a skeleton

Currently used for documentation; entity interfaces don't extend them yet.
UI checks entity types directly (`entity is IActor`).

Future: Entities extend capability interfaces for cleaner type checking.

---

## Why This Architecture?

### Over Event-Driven UI
- **Simpler debugging** - Follow method calls, not event subscriptions
- **Less boilerplate** - No intent events, no caching from events
- **Standard pattern** - DI is widely understood, battle-tested
- **Better testability** - Mock service interfaces directly

### Events Still Valuable For
- Cross-cutting concerns (history needs to know about all transforms)
- System-wide state changes (GPose affects everything)
- Loose coupling where needed (skeleton overlay doesn't care who selected)
