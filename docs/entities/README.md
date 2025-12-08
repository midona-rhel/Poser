# Poser Entities

Entities are the core data structures representing things that can be selected, transformed, and manipulated.

## Entity Hierarchy

```
IEntity (base interface)
├── IActor (game character)
│   ├── implements ITransformable
│   ├── implements IAnimatable
│   ├── implements IGazeable
│   └── implements ISkeletonOwner
│
├── ISkeleton (bone container)
│   └── owned by IActor
│
├── IBone (individual bone)
│   └── implements ITransformable
│
└── ICategory (bone grouping)
    └── contains multiple IBone references
```

## Base Interface

```csharp
public interface IEntity
{
    EntityId Id { get; }
    string Name { get; }
    IEntity? Parent { get; }
    IReadOnlyList<IEntity> Children { get; }
    bool IsSelected { get; set; }
    bool IsVisible { get; }
    bool IsCollapsed { get; set; }
    EntityType EntityType { get; }
}

public enum EntityType
{
    Actor,
    Skeleton,
    Bone,
    Category,
    Camera
}
```

## Capability Interfaces

Capabilities define what an entity can do. Entities implement capabilities via interface inheritance.

### ITransformable

```csharp
public interface ITransformable
{
    Transform Transform { get; }
    void SetTransform(Transform transform);
}
```

Implemented by: `IActor`, `IBone`

### IAnimatable

```csharp
public interface IAnimatable
{
    bool IsFrozen { get; }
    float AnimationSpeed { get; }
    void Freeze();
    void Unfreeze();
    void SetSpeed(float speed);
}
```

Implemented by: `IActor`

### IGazeable

```csharp
public interface IGazeable
{
    bool IsGazeLocked { get; }
    GazeState GazeState { get; }
    void LockGaze(GazeTargetType targetType);
    void UnlockGaze();
}
```

Implemented by: `IActor`

### ISkeletonOwner

```csharp
public interface ISkeletonOwner
{
    ISkeleton? Skeleton { get; }
}
```

Implemented by: `IActor`

## Individual Entity Documentation

- [Actor](./Actor.md) - Game characters
- [Skeleton](./Skeleton.md) - Bone hierarchies
- [Bone](./Bone.md) - Individual bones
- [Category](./Category.md) - Bone groupings

## Entity vs Brio Capability Pattern

**Brio's approach:**
- Capabilities are separate objects attached at runtime
- `entity.AddCapability<PosingCapability>()`
- Query: `entity.TryGetCapability<PosingCapability>(out var cap)`

**Poser's approach:**
- Capabilities are interfaces implemented by entity types
- `public interface IActor : IEntity, ITransformable, IAnimatable`
- Query: `if (entity is ITransformable t) { ... }`

**Why different?**

Poser aims to be a **library** that can be extracted. Compile-time interfaces are:
- Easier to test (mock interfaces)
- Clearer contracts (what can this entity do?)
- No runtime capability lookup
- Simpler dependency graph
- Better IDE support (intellisense, refactoring)

The tradeoff is less runtime flexibility - you can't add capabilities dynamically. For Poser's use case, this is acceptable since entity capabilities are fixed by type.
