# BonePosingService

**Interface:** `IBonePosingService`
**Implementation:** `BonePosingService`
**Location:** `Poser/Game/BonePosingService.cs`

## Purpose

Applies bone transform modifications via game hooks. Maintains a cache of bone modifications and applies them during the game's skeleton update cycle.

## Interface

```csharp
public interface IBonePosingService
{
    /// <summary>
    /// Apply a transform delta to a bone.
    /// </summary>
    void ApplyTransform(IBone bone, Transform delta, IActor? specificActor = null,
                        TransformComponents components = TransformComponents.All);

    /// <summary>
    /// Get current modification for a bone.
    /// </summary>
    Transform? GetModification(IBone bone);

    /// <summary>
    /// Set absolute modification for a bone.
    /// </summary>
    void SetModification(IBone bone, Transform modification);

    /// <summary>
    /// Clear modification for a bone.
    /// </summary>
    void ClearModification(IBone bone);

    /// <summary>
    /// Clear all modifications for a skeleton.
    /// </summary>
    void ClearAllModifications(ISkeleton skeleton);

    /// <summary>
    /// Register skeleton for cache updates.
    /// </summary>
    void RegisterSkeletonForCacheUpdate(ISkeleton skeleton);
}
```

## How It Works

### 1. Store Modifications

When a bone is transformed, the delta is stored in a cache:

```csharp
private readonly Dictionary<ISkeleton, SkeletonPoseInfo> _skeletonPoseInfo = new();

public void ApplyTransform(IBone bone, Transform delta, ...)
{
    var skeleton = bone.Skeleton;
    var poseInfo = GetOrCreatePoseInfo(skeleton);

    var key = (bone.PartialId, bone.BoneIndex);
    var current = poseInfo.GetModification(key) ?? Transform.Identity;
    var newMod = current.ApplyDelta(delta);
    poseInfo.SetModification(key, newMod);
}
```

### 2. Hook Game Update

Two hooks intercept the skeleton update cycle:

```csharp
// Hook 1: After physics update - apply our modifications
private void UpdateBonePhysicsDetour(...)
{
    _hook.Original(...);  // Let game update physics

    // Apply our modifications
    foreach (var (skeleton, poseInfo) in _skeletonPoseInfo)
    {
        ApplyModificationsToGameSkeleton(skeleton, poseInfo);
    }
}

// Hook 2: Before rendering - cache current transforms
private void FinalizeSkeletonsDetour(...)
{
    // Update LastTransform cache for UI
    foreach (var skeleton in _registeredSkeletons)
    {
        UpdateBoneTransformCache(skeleton);
    }

    _hook.Original(...);
}
```

### 3. Apply to Game Memory

```csharp
private void ApplyModificationsToGameSkeleton(ISkeleton skeleton, SkeletonPoseInfo info)
{
    var character = (Character*)skeleton.Actor.Address;
    var renderSkeleton = character->Skeleton;

    foreach (var ((partialId, boneIndex), modification) in info.Modifications)
    {
        var pose = renderSkeleton->PartialSkeletons[partialId].GetHavokPose(0);
        var localSpace = pose->LocalPose;

        ref var boneTransform = ref localSpace[boneIndex];

        // Apply rotation modification
        boneTransform.Rotation = modification.Rotation * boneTransform.Rotation;

        // Apply position modification (scaled)
        boneTransform.Translation += modification.Position.ToHavokVector();

        // Apply scale modification
        if (modification.Scale != Vector3.One)
            boneTransform.Scale *= modification.Scale.ToHavokVector();
    }
}
```

## Hook Signatures

From Brio:
```csharp
// UpdateBonePhysics
"48 8B C4 55 53 56 57 41 54 41 55 41 56 41 57 48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 C7 45 ?? ?? ?? ?? ?? 48 89 58 ?? 0F 29 70 ?? 0F 29 78 ?? 44 0F 29 40 ?? 44 0F 29 48 ?? 44 0F 29 50 ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 48 8B F9"

// FinalizeSkeletons
"E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B C8 E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 8B 49"
```

## Transform Cache

The `LastTransform` property on bones is updated from game memory after each frame:

```csharp
internal void UpdateBoneTransformCache(ISkeleton skeleton)
{
    var character = (Character*)skeleton.Actor.Address;
    var renderSkeleton = character->Skeleton;

    foreach (var bone in skeleton.AllBones)
    {
        var pose = renderSkeleton->PartialSkeletons[bone.PartialId].GetHavokPose(0);
        var transform = pose->LocalPose[bone.BoneIndex];

        bone.SetLastTransform(new Transform
        {
            Position = transform.Translation.ToVector3(),
            Rotation = transform.Rotation.ToQuaternion(),
            Scale = transform.Scale.ToVector3()
        });
    }
}
```

## Events Subscribed

| Event | Action |
|-------|--------|
| `GPoseStateChangedEvent` | Clear all modifications on exit |
| `PosingModeChangedEvent` | Clear modifications when exiting posing mode |

## Brio Reference

Brio's implementation in `SkeletonPosingCapability` and hooks in `SkeletonService` provide:
- PoseInfo structure for bone modifications
- Snapshot/restore functionality
- Pose import/export
