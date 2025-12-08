using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Core;

/// <summary>
/// Published when GPose state changes (entering or exiting).
/// </summary>
public record GPoseStateChangedEvent(bool IsGPosing) : IEvent;

/// <summary>
/// Published when an entity is selected.
/// </summary>
public record EntitySelectedEvent(EntityId Id) : IEvent;

/// <summary>
/// Published when entity selection is cleared.
/// </summary>
public record EntityDeselectedEvent : IEvent;

/// <summary>
/// Published when the entity hierarchy changes (actors added/removed).
/// </summary>
public record EntityHierarchyChangedEvent : IEvent;

/// <summary>
/// Published when the actor list changes (actors added/removed from GPose).
/// Replaces ActorManager.OnActorsChanged direct event.
/// </summary>
public record ActorListChangedEvent(IReadOnlyList<IActor> Actors) : IEvent;

/// <summary>
/// Published when actor selection changes.
/// Replaces ActorManager.OnSelectionChanged direct event.
/// </summary>
public record SelectionChangedEvent(IReadOnlyList<IActor> SelectedActors) : IEvent;

/// <summary>
/// Published when an actor's animation freeze state changes.
/// Replaces AnimationService.OnFreezeStateChanged direct event.
/// </summary>
public record FreezeStateChangedEvent(IActor Actor, bool IsFrozen) : IEvent;

/// <summary>
/// Published when physics freeze state changes (global).
/// Replaces AnimationService.OnPhysicsFreezeStateChanged direct event.
/// </summary>
public record PhysicsFreezeStateChangedEvent(bool IsFrozen) : IEvent;

/// <summary>
/// Published when an actor's transform is modified via posing.
/// </summary>
public record TransformChangedEvent(IActor Actor, Transform NewTransform) : IEvent;

/// <summary>
/// Published when bone selection changes in the editor.
/// </summary>
public record BoneSelectionChangedEvent(IBone? SelectedBone) : IEvent;

/// <summary>
/// Published when posing mode is entered or exited.
/// </summary>
public record PosingModeChangedEvent(bool IsPosingMode) : IEvent;

/// <summary>
/// Published when an actor's gaze lock state changes.
/// </summary>
public record GazeLockChangedEvent(IActor Actor, bool IsLocked) : IEvent;
