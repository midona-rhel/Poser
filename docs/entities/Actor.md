# Actor Entity

**Interface:** `IActor`
**Implementation:** `ActorBase`
**Location:** `Poser/Entities/ActorBase.cs`

## Purpose

Represents a game character (player, NPC, companion, etc.) that can be posed and animated.

## Interface

```csharp
public interface IActor : IEntity, ITransformable, IAnimatable, IGazeable, ISkeletonOwner
{
    /// <summary>
    /// Memory address of the game object.
    /// </summary>
    nint Address { get; }

    /// <summary>
    /// Type of actor (Player, BattleNpc, EventNpc, Companion, etc.)
    /// </summary>
    ActorKind ActorKind { get; }

    /// <summary>
    /// Whether this is a companion (minion, mount, pet).
    /// </summary>
    bool IsCompanion { get; }

    /// <summary>
    /// Whether actor is currently in posing mode.
    /// </summary>
    bool IsPosing { get; }

    /// <summary>
    /// Whether actor transform is being edited.
    /// </summary>
    bool IsEditMode { get; }
}
```

## Capabilities

IActor implements all major capabilities:

| Capability | Purpose |
|------------|---------|
| `ITransformable` | Position, rotation, scale |
| `IAnimatable` | Freeze, speed, animations |
| `IGazeable` | Eye/head/body tracking |
| `ISkeletonOwner` | Access to skeleton |

## Actor Kinds

```csharp
public enum ActorKind
{
    Player,      // Player character
    BattleNpc,   // Enemies, allies
    EventNpc,    // NPCs in cutscenes/events
    Companion,   // Minions, mounts, pets
    Prop,        // Objects (limited functionality)
    Unknown
}
```

## Companion Limitations

Companions have reduced functionality:
- Cannot change base animation
- Animation speed may not work
- Limited pose options

Always check `actor.IsCompanion` before applying animations:

```csharp
if (!actor.IsCompanion)
{
    _animationService.ApplyBaseAnimation(actor, animationId);
}
```

## Properties Panel Display

When an Actor is the primary selection:
- **Transform tab**: Position, rotation, scale sliders
- **Animation tab**: Speed, scrub, base/blend animation selectors
- **Gaze section**: Mode, target type, target entity

## Creation

Actors are created by `IActorManager` when characters enter GPose:

```csharp
// ActorManager detects character spawn
var actor = new ActorBase(gameObject);
Actors.Add(actor);
_eventBus.Publish(new ActorListChangedEvent(Actors));
```

## Memory Layout

The actor holds a pointer to the game's `Character` structure:

```csharp
public unsafe struct Character
{
    public GameObject GameObject;
    public CharacterData CharacterData;
    public ActionTimelineManager Timeline;  // Animation control
    public LookAtController LookAt;         // Gaze control
    public RenderSkeleton* Skeleton;        // Bone data
}
```

## Transform Handling

Actor transforms are managed by `IPosingService`:

```csharp
// Get current transform
var transform = _posingService.GetEffectiveTransform(actor);

// Set override (hooks prevent game from resetting)
_posingService.SetTransformOverride(actor, newTransform);

// Clear override (return to game control)
_posingService.ClearTransformOverride(actor);
```

## Events

Actors publish events through their services:
- `FreezeStateChangedEvent` - When frozen/unfrozen
- `TransformChangedEvent` - When position changes
- `GazeStateChangedEvent` - When gaze changes
