# AnimationService

**Interface:** `IAnimationService`
**Implementation:** `AnimationService`
**Location:** `Poser/Game/AnimationService.cs`

## Purpose

Controls actor animations: freeze/unfreeze, playback speed, time scrubbing, and base/blend animation overrides.

## Interface

```csharp
public interface IAnimationService
{
    // Freeze control
    bool IsFrozen(IActor actor);
    void Freeze(IActor actor);
    void Unfreeze(IActor actor);

    // Speed control
    float GetSpeed(IActor actor);
    void SetSpeed(IActor actor, float speed);

    // Time scrubbing (only works when frozen)
    float? GetAnimationTime(IActor actor);
    float? GetAnimationDuration(IActor actor);
    void SetAnimationTime(IActor actor, float time);

    // Animation overrides
    bool HasBaseOverride(IActor actor);
    void ApplyBaseAnimation(IActor actor, ushort animationId, bool interrupt = true);
    void StopBaseAnimation(IActor actor);
    void PlayBlendAnimation(IActor actor, ushort animationId);
}
```

## Freeze Mechanism

Freezing works by setting the actor's animation speed to 0:

```csharp
public void Freeze(IActor actor)
{
    if (actor.Address == nint.Zero) return;

    var character = (Character*)actor.Address;
    var timeline = character->Timeline;

    // Store original speed
    _originalSpeeds[actor] = timeline.TimelineSequencer.SpeedMultiplier;

    // Set speed to 0
    timeline.TimelineSequencer.SpeedMultiplier = 0f;

    _eventBus.Publish(new FreezeStateChangedEvent(actor, true));
}
```

## Animation Timeline

FFXIV characters have an `ActionTimelineManager` that controls:
- Base animation (idle, sitting, etc.)
- Blend animations (emotes, overlays)
- Animation speed multiplier

### Brio Reference

From Brio's `ActionTimelineCapability`:
```csharp
// Base animation slot
character->Timeline.BaseOverride = animationId;

// Speed multiplier
character->Timeline.TimelineSequencer.SpeedMultiplier = speed;

// Animation time (for scrubbing)
character->Timeline.TimelineSequencer.TimeOffset = time;
```

## Events Published

| Event | When |
|-------|------|
| `FreezeStateChangedEvent` | Actor frozen or unfrozen |
| `AnimationSpeedChangedEvent` | Speed changed |

## Events Subscribed

| Event | Action |
|-------|--------|
| `GPoseStateChangedEvent` | Reset all actors on GPose exit |

## Scrubbing Limitations

- Scrubbing only works when the actor is frozen (speed = 0)
- Not all animations support scrubbing
- Duration may not be available for looping animations

## Companion Limitations

Companions (minions, mounts, pets) have limited animation control:
- Cannot change base animation
- Speed control may not work
- Check `actor.IsCompanion` before applying animations
