# Event Flow Architecture

## Principle: UI Emits Intents, Services Handle Logic

The UI should NEVER directly call service methods that mutate state. Instead:
1. UI emits an **intent event** (user wants to do X)
2. Appropriate service handles the event
3. Service emits a **result event** (X happened)
4. UI listens to result events to update display

This decouples UI from business logic and makes the system testable.

---

## Current Problems

Analyzing the current codebase, UI components directly call services:

### PropertiesPanel.cs
```csharp
// WRONG: Direct service calls
_animationService.SetSpeed(actor, speed);
_posingService.SetTransformOverride(actor, transform);
_controller.SetGazeMode(actor, mode);  // Controller is better, but still direct
```

### GizmoOverlayWindow.cs
```csharp
// WRONG: Direct service calls
_posingService.SetTransformOverride(actor, actorTransform);
_bonePosingService.ApplyTransform(bone, delta, null, TransformComponents.All);
```

### EntityList.cs
```csharp
// WRONG: Direct state manipulation
_editorState.Select(entity);
_editorState.AddToSelection(entity);
```

---

## Proposed Event Flow

### Selection Events

```
USER ACTION                    INTENT EVENT                      HANDLER
─────────────────────────────────────────────────────────────────────────
Click entity                → SelectEntityIntent(entity)       → SelectionService
Ctrl+Click entity           → AddToSelectionIntent(entity)     → SelectionService
Shift+Click entity          → SelectRangeIntent(from, to)      → SelectionService
Click empty area            → ClearSelectionIntent()           → SelectionService

RESULT EVENT                   LISTENERS
─────────────────────────────────────────────────────────────────────────
SelectionChangedEvent        → PropertiesPanel (update display)
                             → GizmoOverlay (update target)
                             → EntityList (update highlighting)
```

### Transform Events

```
USER ACTION                    INTENT EVENT                      HANDLER
─────────────────────────────────────────────────────────────────────────
Drag gizmo start            → TransformDragStartIntent(entities)  → HistoryService (begin recording)
Drag gizmo                  → TransformIntent(entity, delta)      → TransformService
Drag gizmo end              → TransformDragEndIntent()            → HistoryService (create action)
Edit transform in panel     → TransformIntent(entity, absolute)   → TransformService

RESULT EVENT                   LISTENERS
─────────────────────────────────────────────────────────────────────────
TransformChangedEvent        → PropertiesPanel (update display)
                             → GizmoOverlay (update position)
                             → HistoryService (record for undo)
```

### Animation Events

```
USER ACTION                    INTENT EVENT                      HANDLER
─────────────────────────────────────────────────────────────────────────
Click freeze button         → FreezeIntent(actor)               → AnimationService
Click unfreeze button       → UnfreezeIntent(actor)             → AnimationService
Adjust speed slider         → SetSpeedIntent(actor, speed)      → AnimationService
Select animation            → PlayAnimationIntent(actor, id)    → AnimationService

RESULT EVENT                   LISTENERS
─────────────────────────────────────────────────────────────────────────
FreezeStateChangedEvent      → PropertiesPanel (update button state)
                             → TopBar (update pose toggle)
SpeedChangedEvent            → PropertiesPanel (update slider)
```

### Gaze Events

```
USER ACTION                    INTENT EVENT                      HANDLER
─────────────────────────────────────────────────────────────────────────
Change gaze mode            → SetGazeModeIntent(actor, mode)    → GazeService
Change gaze target          → SetGazeTargetIntent(actor, target)→ GazeService
Select gaze bone            → (auto) LockGazeIntent(actor, type)→ GazeService

RESULT EVENT                   LISTENERS
─────────────────────────────────────────────────────────────────────────
GazeStateChangedEvent        → PropertiesPanel (update controls)
GazeLockChangedEvent         → PropertiesPanel (update lock indicator)
```

### Posing Mode Events

```
USER ACTION                    INTENT EVENT                      HANDLER
─────────────────────────────────────────────────────────────────────────
Click pose toggle           → TogglePosingModeIntent()          → PosingModeService
Select bone (auto-enter)    → EnterPosingModeIntent()           → PosingModeService

RESULT EVENT                   LISTENERS
─────────────────────────────────────────────────────────────────────────
PosingModeChangedEvent       → TopBar (update toggle state)
                             → AnimationService (freeze all)
                             → GazeService (lock all)
```

---

## Intent vs Result Events

### Intent Events (UI → Service)
- Named with `Intent` suffix
- Represent user's desire to do something
- May be rejected/ignored by service
- UI should NOT assume success

```csharp
public record SelectEntityIntent(IEntity Entity) : IEvent;
public record TransformIntent(IEntity Entity, Transform Delta) : IEvent;
public record FreezeIntent(IActor Actor) : IEvent;
```

### Result Events (Service → UI)
- Named with past tense or `Changed` suffix
- Represent something that actually happened
- UI updates display based on these
- Used by HistoryService for undo

```csharp
public record SelectionChangedEvent(IReadOnlyList<IEntity> Selected) : IEvent;
public record TransformChangedEvent(IEntity Entity, Transform Old, Transform New) : IEvent;
public record FreezeStateChangedEvent(IActor Actor, bool IsFrozen) : IEvent;
```

---

## Implementation Priority

### Phase 1: Selection (Foundation)
1. Create intent events for selection
2. SelectionService subscribes to intents
3. Update EntityList to emit intents instead of direct calls
4. SelectionService emits SelectionChangedEvent
5. PropertiesPanel/Gizmo listen to result event

### Phase 2: Transform
1. Create intent events for transform
2. TransformService subscribes to intents
3. Update GizmoOverlay to emit intents
4. TransformService dispatches to PosingService/BonePosingService
5. Emit TransformChangedEvent

### Phase 3: Animation/Gaze
1. Create intent events
2. Services subscribe
3. Update PropertiesPanel to emit intents
4. Services emit result events

---

## Benefits

1. **Testable**: Can test services by publishing intent events
2. **Decoupled**: UI doesn't know about service implementations
3. **Auditable**: Can log all intents for debugging
4. **Undoable**: HistoryService can intercept all changes
5. **Extensible**: New UI can emit same intents
