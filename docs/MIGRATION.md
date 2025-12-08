# Migration Plan: Current → Proposed Architecture

## Overview

This document outlines the migration path from the current architecture to the proposed entity-capability pattern.

---

## Phase 1: Consolidate Selection (Foundation)

**Goal:** Single source of truth for all selection.

### Tasks

1. **Create ISelectionService interface**
   - File: `Poser/Services/ISelectionService.cs`
   - Methods: Select, AddToSelection, RemoveFromSelection, etc.

2. **Create SelectionService implementation**
   - File: `Poser/Services/SelectionService.cs`
   - Move selection logic from EditorState

3. **Update EditorState**
   - Remove: `_selectedEntities`, `_lastSelectedEntity`
   - Remove: Selection methods (Select, AddToSelection, etc.)
   - Keep: `TransformPivot`, `TransformOrientation`, `TransformTool`, `IsPosingMode`
   - Inject: `ISelectionService`

4. **Update ActorManager**
   - Remove: `SelectedActors`, `PrimarySelectedActor`
   - Remove: Selection events
   - Keep: Actor lifecycle only

5. **Update all UI components**
   - Replace `_actorManager.PrimarySelectedActor` → `_selectionService.Primary`
   - Replace `_editorState.SelectedEntities` → `_selectionService.Selected`

### Files Modified
- `Poser/Services/ISelectionService.cs` (new)
- `Poser/Services/SelectionService.cs` (new)
- `Poser/Core/EditorState.cs`
- `Poser/Services/ActorManager.cs`
- `Poser/UI/Components/PropertiesPanel.cs`
- `Poser/UI/Components/EntityList.cs`
- `Poser/UI/GizmoOverlayWindow.cs`

---

## Phase 2: Add Capability Interfaces

**Goal:** Entities declare capabilities via interfaces, enabling capability-based property display.

### Tasks

1. **Create capability interfaces**
   - File: `Poser/Entities/Capabilities/ITransformable.cs`
   - File: `Poser/Entities/Capabilities/IAnimatable.cs`
   - File: `Poser/Entities/Capabilities/IGazeable.cs`
   - File: `Poser/Entities/Capabilities/ISkeletonOwner.cs`

2. **Update IActor to extend capabilities**
   ```csharp
   public interface IActor : IEntity, ITransformable, IAnimatable, IGazeable, ISkeletonOwner
   ```

3. **Update IBone to extend ITransformable**
   ```csharp
   public interface IBone : IEntity, ITransformable
   ```

4. **Create ICategory as IEntity**
   - File: `Poser/Entities/ICategory.cs`
   - File: `Poser/Entities/Category.cs`

5. **Update PropertiesPanel**
   - Check `primary is ITransformable` instead of type
   - Check `primary is IAnimatable` instead of type
   - Check `primary is IGazeable` instead of type

### Files Modified
- `Poser/Entities/Capabilities/` (new folder)
- `Poser/Entities/IActor.cs`
- `Poser/Entities/IBone.cs`
- `Poser/Entities/ICategory.cs` (new)
- `Poser/Entities/Category.cs` (new)
- `Poser/UI/Components/PropertiesPanel.cs`

---

## Phase 3: Create TransformService

**Goal:** Unified transform handling with type-based dispatch.

### Tasks

1. **Create ITransformService interface**
   - File: `Poser/Services/ITransformService.cs`

2. **Create TransformService implementation**
   - File: `Poser/Services/TransformService.cs`
   - Delegates to PosingService (actors) and BonePosingService (bones)

3. **Update GizmoOverlayWindow**
   - Use `_transformService.ApplyTransform(entity, delta)` instead of direct service calls

4. **Update PropertiesPanel**
   - Use TransformService for transform changes

### Files Modified
- `Poser/Services/ITransformService.cs` (new)
- `Poser/Services/TransformService.cs` (new)
- `Poser/UI/GizmoOverlayWindow.cs`
- `Poser/UI/Components/PropertiesPanel.cs`

---

## Phase 4: Event-Driven History

**Goal:** HistoryService collects events automatically.

### Tasks

1. **Add transform events**
   - `TransformDragStartedEvent`
   - `TransformDragEndedEvent`

2. **Update HistoryService**
   - Subscribe to transform events
   - Collect changes between Start/End
   - Create composite action on End

3. **Update GizmoOverlayWindow**
   - Publish start/end events instead of creating actions directly

### Files Modified
- `Poser/Core/Events.cs`
- `Poser/History/HistoryService.cs`
- `Poser/UI/GizmoOverlayWindow.cs`

---

## Phase 5: Extract Core Library (Future)

**Goal:** Separate library from plugin.

### Project Structure

```
Poser.Core/
├── Entities/
│   ├── IEntity.cs
│   ├── IActor.cs
│   ├── IBone.cs
│   ├── ISkeleton.cs
│   └── ICategory.cs
├── Capabilities/
│   ├── ITransformable.cs
│   ├── IAnimatable.cs
│   └── IGazeable.cs
├── Services/
│   ├── ISelectionService.cs
│   ├── ITransformService.cs
│   ├── IAnimationService.cs
│   └── IGazeService.cs
├── Events/
│   └── Events.cs
└── Transform.cs

Poser.Game/
├── Services/
│   ├── AnimationService.cs
│   ├── GazeService.cs
│   ├── PosingService.cs
│   └── BonePosingService.cs
├── Hooks/
└── Native/

Poser.Plugin/
├── UI/
├── Controllers/
└── Plugin.cs
```

---

## Immediate Next Steps

Before starting the migration, we should:

1. **Fix current compilation** - Ensure current code builds
2. **Clean up duplicates** - Remove ActorLookAtService files
3. **Document current state** - This is done (docs folder)
4. **Start Phase 1** - Create SelectionService

---

## Testing Strategy

Each phase should be tested before moving to the next:

### Phase 1 Tests
- [ ] Single selection works
- [ ] Multi-selection (Ctrl+click) works
- [ ] Properties panel shows primary selection
- [ ] Gizmo targets selected entities

### Phase 2 Tests
- [ ] Actor shows transform + animation + gaze tabs
- [ ] Bone shows transform only
- [ ] Category shows info

### Phase 3 Tests
- [ ] Actor transform via gizmo works
- [ ] Bone transform via gizmo works
- [ ] Category transform via gizmo works
- [ ] History records all transforms

### Phase 4 Tests
- [ ] Multi-select drag creates single undo action
- [ ] Undo restores all entities
