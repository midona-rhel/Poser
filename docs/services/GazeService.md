# GazeService

**Interface:** `IGazeService`
**Implementation:** `GazeService`
**Location:** `Poser/Game/GazeService.cs`

## Purpose

Controls where actors look (eyes, head, body tracking). Hooks the game's LookAt system to override gaze direction.

## Interface

```csharp
public interface IGazeService
{
    /// <summary>
    /// Get current gaze state for an actor.
    /// </summary>
    GazeState GetGazeState(IActor actor);

    /// <summary>
    /// Set gaze mode (what to look at).
    /// </summary>
    void SetGazeMode(IActor actor, GazeTargetMode mode);

    /// <summary>
    /// Set which body parts are affected by gaze control.
    /// </summary>
    void SetGazeTargetType(IActor actor, GazeTargetType targetType);

    /// <summary>
    /// Set another actor as the gaze target.
    /// </summary>
    void SetGazeTarget(IActor actor, IActor target);

    /// <summary>
    /// Reset gaze to game default.
    /// </summary>
    void ResetGaze(IActor actor);

    /// <summary>
    /// Set complete gaze state (for undo/redo).
    /// </summary>
    void SetGazeState(IActor actor, GazeState state);

    /// <summary>
    /// Lock gaze at current position (prevents game from updating).
    /// </summary>
    void LockGaze(IActor actor, GazeTargetType targetType = GazeTargetType.All);

    /// <summary>
    /// Unlock gaze (allows game to control again).
    /// </summary>
    void UnlockGaze(IActor actor);

    /// <summary>
    /// Check if gaze is locked.
    /// </summary>
    bool IsGazeLocked(IActor actor);
}
```

## Gaze Target Modes

```csharp
public enum GazeTargetMode
{
    /// <summary>No override - use game default</summary>
    None,
    /// <summary>Look straight ahead based on body facing</summary>
    Forward,
    /// <summary>Look at the camera</summary>
    Camera,
    /// <summary>Look at another entity</summary>
    Entity
}
```

## Gaze Target Types (Flags)

```csharp
[Flags]
public enum GazeTargetType
{
    None = 0,
    Body = 1,    // Torso turns toward target
    Head = 4,    // Head turns toward target
    Eyes = 8,    // Eyes look toward target
    All = Body | Head | Eyes
}
```

## Gaze State

```csharp
public class GazeState
{
    public GazeTargetMode Mode { get; set; }
    public GazeTargetType TargetType { get; set; }
    public IActor? TargetEntity { get; set; }

    public GazeState Clone();
}
```

## Game Hook

GazeService hooks the game's `ActorLookAtLoop` function to intercept look-at updates:

```csharp
// Hook signature from Brio
var actorLookAtLoopAddress = sigScanner.ScanText(
    "E8 ?? ?? ?? ?? 48 83 C3 08 48 83 EF 01 75 CF 48 ?? ?? ?? ?? 48");

private nint ActorLookAtDetour(ContainerInterface* args)
{
    if (_gPoseService.IsGPosing)
    {
        // Check if we have override for this actor
        if (_lookAtHandles.TryGetValue(actorId, out var data))
        {
            // Apply our gaze override
            if (data.EyesLocked || data.HeadLocked || data.BodyLocked)
            {
                // Force frozen look mode
                _updateLookAt(controller, &target, index, 0);
                return _hook.Original(args);
            }

            // Apply mode-based targeting
            UpdateTargetPosition(data);
            ApplyLookAt(controller, data);
        }
    }
    return _hook.Original(args);
}
```

## Events Published

| Event | When |
|-------|------|
| `GazeLockChangedEvent` | When gaze is locked or unlocked |
| `GazeStateChangedEvent` | When gaze mode/target changes |

## Events Subscribed

| Event | Action |
|-------|--------|
| `GPoseStateChangedEvent` | Clear all gaze handles on GPose exit |
| `PosingModeChangedEvent` | Lock gaze when entering posing mode |

## Integration with Posing Mode

### Gaze Lock vs Bone Posing Interaction

**Key insight:** Gaze control and bone posing are mutually exclusive for the same bones.

- **When manipulating gaze bones directly** (via bone selection/gizmo): Gaze should be LOCKED (frozen at position) so the game doesn't fight our bone transforms
- **When using gaze control UI** (mode, target): Gaze should be UNLOCKED so the game's look-at system can animate the bones

### Automatic Behavior

```csharp
// When selecting a gaze-related bone (head, neck, eyes)
if (IsGazeBone(bone))
{
    _gazeService.LockGaze(actor, GetGazeTypeForBone(bone));
}

// When using gaze control UI to set target
_gazeService.UnlockGaze(actor);  // Allow game to animate
_gazeService.SetGazeMode(actor, GazeTargetMode.Entity);
_gazeService.SetGazeTarget(actor, targetActor);
```

### Gaze-Related Bones

These bones are controlled by the game's look-at system:
- `j_kao` (face/head)
- `j_kubi` (neck)
- Eye bones
- Upper spine (for body tracking)

When any of these are selected for direct manipulation, gaze should lock.

### State Management

```csharp
public class GazeState
{
    public GazeTargetMode Mode { get; set; }
    public GazeTargetType TargetType { get; set; }
    public IActor? TargetEntity { get; set; }

    // Explicit lock state (separate from mode)
    public bool IsLocked { get; set; }

    // Which parts are locked vs controlled
    public GazeTargetType LockedParts { get; set; }
    public GazeTargetType ControlledParts { get; set; }
}
```

### UI Flow

1. **User selects head bone** → Gaze locks for head, eyes follow head
2. **User rotates head via gizmo** → Gaze stays locked
3. **User clicks "Look at Camera" button** → Gaze unlocks, game animates eyes/head to camera
4. **User selects eye bone** → Gaze locks for eyes only, head continues tracking

## Native Structures

```csharp
internal enum LookMode
{
    None = 0,
    Frozen = 1,    // Gaze locked in place
    Pivot = 2,     // Unknown
    Position = 3   // Track position
}

internal struct LookAtTarget
{
    public LookMode LookMode;      // Offset 0x08
    public Vector3 Position;       // Offset 0x10
}
```
