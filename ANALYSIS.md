# Poser Codebase Analysis & Improvement Suggestions

## Overview

**Poser** is a Dalamud plugin for FFXIV GPose that enables bone-level pose editing. The codebase follows good patterns overall:
- Event-driven architecture with EventBus
- Dependency injection via Microsoft.Extensions.DependencyInjection
- Composite entity pattern for hierarchical entities (Actor → Skeleton → Bones)
- ImGui-based UI with reusable controls

This document outlines observations and potential improvements for consideration.

---

## Potential Issues

### 1. Bone Name Lookup May Return Wrong Bone
**Location:** `Poser/Entities/Skeleton.cs:146-149`

**Observation:** The `_bonesByName` dictionary uses only `boneName` as the key, ignoring `partialId`. When multiple partial skeletons have bones with the same name (common with equipment, mounts, or complex races like Hrothgar/Viera), the lookup returns whichever bone was added first.

```csharp
var uniqueKey = $"{partialId}_{boneName}";  // Computed but not used
if (!_bonesByName.ContainsKey(boneName))
    _bonesByName[boneName] = bone;
```

**Considerations:**
- This may be intentional "first-win" behavior for the common case
- The method `GetBoneByName(string name, int partialId)` at line 59 does handle this correctly
- Could use the composite key instead, or document the intended behavior

### 2. Camera Rotation Not Implemented
**Location:** `Poser/Entities/Camera.cs:22`

**Observation:** Camera rotation returns a hardcoded identity quaternion.

```csharp
public Quaternion Rotation => Quaternion.Identity;  // "not yet implemented"
```

**Considerations:**
- May not be needed for current features
- Could extract from `ICameraService` which has access to `SceneCamera`
- Worth implementing if camera manipulation features are planned

### 3. Hardcoded Memory Offsets
**Location:** `Poser/Entities/Skeleton.cs:309-316`

**Observation:** Scale factor is read using hardcoded memory offsets `0x2A0` and `0x2A4`.

```csharp
var scaleFactor1 = *(float*)(basePtr + 0x2A0);
var scaleFactor2 = *(float*)(basePtr + 0x2A4);
```

**Considerations:**
- Comment references "Brio's BrioCharacterBase offsets" - may need updating with game patches
- Could add validation to fall back to 1.0 if values are invalid (NaN, negative, extreme)
- Worth checking if FFXIVClientStructs exposes these fields directly

### 4. Silent Skeleton Connection Failures
**Location:** `Poser/Entities/Skeleton.cs:187-191`

**Observation:** When partial skeletons fail to connect to their parent, there's no logging or error handling.

```csharp
if (partialBones[0].TryGetValue(connectedParentIndex, out var parentBone) &&
    partialBones[partialIdx].TryGetValue(connectedBoneIndex, out var childBone))
{
    parentBone.AddChildBone(childBone);
}
// No else branch - silent failure
```

**Considerations:**
- Could help debug issues with complex characters
- A warning log would be useful for troubleshooting

### 5. Thread Safety
**Location:** `Poser/Game/ActorManager.cs`

**Observation:** Collections like `_actors` and `_selectedActors` are accessed without synchronization.

**Considerations:**
- Dalamud's framework update runs on the main thread, so this may be safe in practice
- Debug assertions could verify single-thread access
- Worth reviewing if any async operations are added later

---

## Code Organization Observations

### EntityList Complexity
**Location:** `Poser/UI/Components/EntityList.cs` (886 lines)

**Observations:**
- Large file with 9 injected dependencies
- Multiple concerns mixed: entity rendering, category views, selection handling, state management
- Deeply nested rendering methods (7+ levels of depth)

**Possible improvements:**
- Extract category/subcategory collapse state into a dedicated manager
- Split category view rendering into its own class
- Split hierarchy view rendering into its own class
- Consider a facade service to reduce dependency count

### Only First Pose Processed
**Location:** `Poser/Entities/Skeleton.cs:178`

**Observation:** The code supports up to 4 poses per partial skeleton but only processes the first.

```csharp
break; // Only process the first valid pose
```

**Considerations:**
- Poses 1-3 may be blend poses or facial expressions
- Could be worth investigating what these represent if advanced pose blending is desired

---

## Architecture Strengths

- **Clean service abstraction**: Services are behind interfaces, making testing easier
- **Event-driven updates**: UI responds to events rather than polling
- **Composite pattern**: Entity hierarchy is well-modeled
- **DPI awareness**: UI scales properly with `ImGuiHelpers.GlobalScale`
- **Defensive coding**: Null checks and validation throughout

---

## Files of Interest

| File | Notes |
|------|-------|
| `Poser/Entities/Skeleton.cs` | Core skeleton building, bone lookup, transform updates |
| `Poser/Entities/Camera.cs` | Simple entity, rotation not implemented |
| `Poser/Game/ActorManager.cs` | Actor lifecycle and selection management |
| `Poser/UI/Components/EntityList.cs` | Largest UI component, renders entity hierarchy |
| `Poser/UI/Controls/ImPoser.cs` | Reusable ImGui helpers |
| `Poser/Game/CameraService.cs` | Camera access for potential rotation implementation |

---

## Testing Considerations

If changes are made:
- **Bone lookups:** Test with Hrothgar, Viera, characters with weapons/shields
- **Scale factor:** Test with Lalafell, Roegadyn, height-modified characters
- **UI changes:** Test with multiple actors and rapid expand/collapse

---

*This analysis is based on a review of the current codebase state. Suggestions should be evaluated against project goals and priorities.*
