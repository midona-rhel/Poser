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
public record ActorListChangedEvent(IReadOnlyList<ActorBase> Actors) : IEvent;

/// <summary>
/// Published when actor selection changes.
/// Replaces ActorManager.OnSelectionChanged direct event.
/// </summary>
public record SelectionChangedEvent(IReadOnlyList<ActorBase> SelectedActors) : IEvent;

/// <summary>
/// Published when an actor's animation freeze state changes.
/// Replaces AnimationService.OnFreezeStateChanged direct event.
/// </summary>
public record FreezeStateChangedEvent(ActorBase Actor, bool IsFrozen) : IEvent;

/// <summary>
/// Published when physics freeze state changes (global).
/// Replaces AnimationService.OnPhysicsFreezeStateChanged direct event.
/// </summary>
public record PhysicsFreezeStateChangedEvent(bool IsFrozen) : IEvent;

/// <summary>
/// Published when an actor's transform is modified via posing.
/// </summary>
public record TransformChangedEvent(ActorBase Actor, Transform NewTransform) : IEvent;
