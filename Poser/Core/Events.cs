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
/// Published when the entity hierarchy changes (actors added/removed).
/// </summary>
public record EntityHierarchyChangedEvent : IEvent;

/// <summary>
/// Published when the actor list changes (actors added/removed from GPose).
/// </summary>
public record ActorListChangedEvent(IReadOnlyList<IActor> Actors) : IEvent;

/// <summary>
/// Published when posing mode is entered or exited.
/// </summary>
public record PosingModeChangedEvent(bool IsPosingMode) : IEvent;

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

/// <summary>
/// Published when an entity's transform is modified.
/// Contains old transform for undo support.
/// </summary>
public record TransformChangedEvent(IEntity Entity, Transform OldTransform, Transform NewTransform) : IEvent;

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

/// <summary>
/// Published when an actor's animation speed changes.
/// </summary>
public record SpeedChangedEvent(IActor Actor, float Speed) : IEvent;

#endregion

#region Gaze Events (for History)

/// <summary>
/// Published when an actor's gaze lock state changes.
/// </summary>
public record GazeLockChangedEvent(IActor Actor, bool IsLocked) : IEvent;

/// <summary>
/// Published when an actor's gaze state changes (mode, target, etc.).
/// </summary>
public record GazeStateChangedEvent(IActor Actor, GazeState State) : IEvent;

#endregion

#region Editor Settings Events

/// <summary>
/// Published when editor settings change (pivot, orientation, tool).
/// </summary>
public record EditorSettingsChangedEvent(
    TransformPivot Pivot,
    TransformOrientation Orientation,
    TransformTool Tool) : IEvent;

#endregion
