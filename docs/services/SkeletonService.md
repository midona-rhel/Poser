# SkeletonService

**Interface:** `ISkeletonService`
**Implementation:** `SkeletonService`
**Location:** `Poser/Game/SkeletonService.cs`

## Purpose

Creates and caches skeleton structures from game memory. Converts game skeleton data into our `Skeleton` and `Bone` entities.

## Interface

```csharp
public interface ISkeletonService
{
    /// <summary>
    /// Get or create skeleton for an actor.
    /// </summary>
    ISkeleton? GetSkeleton(IActor actor);

    /// <summary>
    /// Force refresh skeleton from game memory.
    /// </summary>
    void RefreshSkeleton(IActor actor);

    /// <summary>
    /// Clear cached skeleton for actor.
    /// </summary>
    void ClearSkeleton(IActor actor);
}
```

## Skeleton Structure

FFXIV skeletons have a complex structure with **partial skeletons**:

```
Character Skeleton (Partial 0)
├── Root bone (n_root)
│   └── Body bones (spine, arms, legs, etc.)
│
Equipment Skeleton (Partial 1)  [hidden in UI]
├── Hair bones
├── Hat bones
│
Weapon Skeleton (Partial 2)  [hidden in UI]
├── Main hand bones
├── Off hand bones
│
Additional Partials...
```

### Partial Skeletons

Each partial skeleton contains:
- **4 poses** (only first is typically used)
- **Connected bone index** linking to parent partial
- **Bone array** with parent/child relationships

```csharp
// From game memory
public unsafe struct PartialSkeleton
{
    public hkaPose* Poses;           // 4 poses
    public short ConnectedBoneIndex; // Link to parent partial
    public short ConnectedParentBoneIndex;
}
```

## Bone Building

```csharp
private Skeleton BuildFromGameSkeleton(Character* character)
{
    var skeleton = new Skeleton(actor);
    var renderSkeleton = character->Skeleton;

    for (int partialId = 0; partialId < renderSkeleton->PartialSkeletonCount; partialId++)
    {
        var partial = renderSkeleton->PartialSkeletons[partialId];
        var pose = partial.GetHavokPose(0);

        for (int boneIdx = 0; boneIdx < pose->Skeleton->Bones.Length; boneIdx++)
        {
            var boneName = pose->Skeleton->Bones[boneIdx].Name;
            var bone = new Bone(boneName, boneIdx, partialId, skeleton);

            // Build parent-child relationships
            var parentIdx = pose->Skeleton->ParentIndices[boneIdx];
            if (parentIdx >= 0)
                bone.SetParent(GetBone(partialId, parentIdx));

            skeleton.AddBone(bone);
        }

        // Connect partial roots to parent partial
        ConnectPartialRoot(skeleton, partial, partialId);
    }

    return skeleton;
}
```

## Bone Lookup

Bones can be looked up multiple ways:

```csharp
// By name (returns first match - may be ambiguous!)
var bone = skeleton.GetBone("j_kosi");

// By partial + index (unique)
var bone = skeleton.GetBone(partialId: 0, boneIndex: 5);

// By name + partial (unique)
var bone = skeleton.GetBoneByName("j_kosi", partialId: 0);
```

**Warning:** `GetBone(string name)` returns the first match. Characters with equipment may have duplicate bone names across partials.

## Caching

Skeletons are cached per actor to avoid rebuilding every frame:

```csharp
private readonly Dictionary<nint, Skeleton> _skeletonCache = new();

public ISkeleton? GetSkeleton(IActor actor)
{
    if (_skeletonCache.TryGetValue(actor.Address, out var cached))
        return cached;

    var skeleton = BuildFromGameSkeleton(actor);
    _skeletonCache[actor.Address] = skeleton;
    return skeleton;
}
```

## Events Subscribed

| Event | Action |
|-------|--------|
| `GPoseStateChangedEvent` | Clear all cached skeletons on exit |
| `ActorListChangedEvent` | Clear skeleton for removed actors |

## Brio Reference

Brio's `SkeletonService` provides additional features:
- Physics enable/disable
- Skeleton update hooks for posing
- Character pose structure management

From `Brio/Game/Posing/SkeletonService.cs`:
```csharp
public delegate void SkeletonUpdateEvent();
public event SkeletonUpdateEvent? SkeletonUpdateStart;
public event SkeletonUpdateEvent? SkeletonUpdateEnd;
```
