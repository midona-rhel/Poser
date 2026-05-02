using System.Collections.Generic;
using Poser.Entities;
using Poser.Services;

namespace Poser.Core;

// =============================================================================
// SYSTEM EVENTS
// =============================================================================
// Events for cross-cutting concerns: system state changes and history recording.
// Services emit these; UI and HistoryService subscribe.

#region System Events

/// <summary>
/// Published when GPose state changes (entering or exiting).
/// </summary>
public record GPoseStateChangedEvent(bool IsGPosing) : IEvent;

/// <summary>
/// Published when the actor list changes (actors added/removed from GPose).
/// </summary>
public record ActorListChangedEvent(IReadOnlyList<IActor> Actors) : IEvent;

#endregion

#region Selection Events

/// <summary>
/// Published when selection changes. Contains ALL currently selected entities.
/// Used by components that need to react to selection changes (e.g., skeleton overlay).
/// </summary>
public record SelectionChangedEvent(IReadOnlyList<IEntity> Selected) : IEvent;

/// <summary>
/// Published when bone selection changes specifically.
/// Used for backwards compatibility and focused bone selection handling.
/// </summary>
public record BoneSelectionChangedEvent(IBone? SelectedBone) : IEvent;

#endregion

#region Transform Events (for History)

/// <summary>
/// Published when transform drag operation starts.
/// HistoryService uses this to begin recording.
/// </summary>
public record TransformDragStartedEvent(IReadOnlyList<IEntity> Entities) : IEvent;

/// <summary>
/// Published when transform drag operation ends.
/// HistoryService uses this to create undo action.
/// </summary>
public record TransformDragEndedEvent : IEvent;

#endregion

#region Animation Events (for History)

/// <summary>
/// Published when an actor's animation freeze state changes.
/// </summary>
public record FreezeStateChangedEvent(IActor Actor, bool IsFrozen) : IEvent;

/// <summary>
/// Published when physics freeze state changes (global).
/// </summary>
public record PhysicsFreezeStateChangedEvent(bool IsFrozen) : IEvent;

#endregion

#region Gaze Events (for History)

/// <summary>
/// Published when an actor's gaze lock state changes.
/// </summary>
public record GazeLockChangedEvent(IActor Actor, bool IsLocked) : IEvent;

#endregion

#region Service Events

/// <summary>
/// Published when virtual camera list changes.
/// </summary>
public record CamerasChangedEvent : IEvent;

/// <summary>
/// Published when history state changes (undo/redo stack modified).
/// </summary>
public record HistoryChangedEvent : IEvent;

/// <summary>
/// Published when light list changes (light spawned/destroyed).
/// </summary>
public record LightsChangedEvent : IEvent;

/// <summary>
/// Published when reference image list changes.
/// </summary>
public record ImagesChangedEvent : IEvent;

/// <summary>
/// Published when a bone's transform changes during posing.
/// </summary>
public record BoneTransformChangedEvent(IBone Bone) : IEvent;

/// <summary>
/// Published when pose library is refreshed.
/// </summary>
public record LibraryRefreshedEvent : IEvent;

/// <summary>
/// Published when library favorites change.
/// </summary>
public record FavoritesChangedEvent : IEvent;

/// <summary>
/// Published during library scan with progress info.
/// </summary>
public record ScanProgressEvent(float Progress, string Message) : IEvent;

#endregion
