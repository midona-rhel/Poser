using System.Collections.Generic;
using Poser.Entities;
using Poser.Services;

namespace Poser.Core;

// =============================================================================
// SYSTEM EVENTS
// =============================================================================
// Transitional events used by the retained runtime and selection adapters.

#region System Events

/// <summary>
/// Published when GPose state changes (entering or exiting).
/// </summary>
public record GPoseStateChangedEvent(bool IsGPosing) : IEvent;

/// <summary>
/// Published when the actor list changes (actors added/removed from GPose).
/// </summary>
public record ActorListChangedEvent(IReadOnlyList<IActor> Actors) : IEvent;

/// <summary>
/// Published after an actor's skeleton has been created or rebuilt.
/// Selection consumers use this boundary to replace stale bone references with
/// the matching bone from the current skeleton.
/// </summary>
public record SkeletonChangedEvent(IActor Actor, ISkeleton? Skeleton) : IEvent;

#endregion

#region Selection Events

/// <summary>
/// Published when selection changes. Contains ALL currently selected entities.
/// Used by components that need to react to selection changes (e.g., skeleton overlay).
/// </summary>

/// <summary>
/// Published when bone selection changes specifically.
/// Used for backwards compatibility and focused bone selection handling.
/// </summary>

#endregion

#region Service Events

/// <summary>
/// Published when a bone's transform changes during posing.
/// </summary>
public record BoneTransformChangedEvent(IBone Bone) : IEvent;

/// <summary>
/// A gaze entry's mode changed (any actor). Consumers re-read state from
/// IGazeService; the payload stays empty so the native-thread publisher never
/// marshals actor references.
/// </summary>
public record GazeStateChangedEvent : IEvent;

#endregion
