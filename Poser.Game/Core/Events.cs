using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Application.Events;

namespace Poser.Core;

// =============================================================================
// SYSTEM EVENTS
// =============================================================================
// Transitional events used by the retained runtime and selection adapters.

#region System Events

/// <summary>Final capture has finished; restore owned native values before
/// GPoseStateChangedEvent(false) removes actors and their bindings.</summary>
public record GPoseExitingEvent : IEvent;

/// <summary>One present actor, as a value: what presence subscribers prune
/// their state against. Never the wrapper itself.</summary>
public readonly record struct ActorPresence(EntityId Id, nint Address);

/// <summary>
/// Published when the actor list changes (actors added/removed from GPose, or
/// a row field such as visibility). The payload is a copy taken at publish
/// time: a subscriber that refreshes the list during dispatch cannot change
/// what later subscribers see.
/// </summary>
public record ActorListChangedEvent(IReadOnlyList<ActorPresence> Actors) : IEvent
{
    public static ActorListChangedEvent Of(IEnumerable<IActor> actors) =>
        new(actors.Select(actor => new ActorPresence(actor.Id, actor.Address)).ToArray());
}

/// <summary>
/// Published on the framework update after an actor's slot skeleton was
/// created, rebuilt (<paramref name="Present"/> true) or released. Never
/// published from inside the native hook that discovered the change.
/// </summary>
public record SkeletonChangedEvent(EntityId Actor, PoseSlot Slot, bool Present) : IEvent;

/// <summary>
/// Published when the spawned-light list changes (light spawned or destroyed).
/// Payload-free: subscribers re-read the list.
/// </summary>
public record LightListChangedEvent : IEvent;

/// <summary>
/// Published when the spawned-prop list changes (prop spawned or destroyed).
/// Carries no payload — the prop handle type lives above this assembly, and
/// every subscriber re-reads the live list anyway.
/// </summary>
public record PropListChangedEvent : IEvent;

/// <summary>
/// Published when the overlay-node list changes (a game-UI overlay node
/// created or destroyed). Payload-free for the same reason the prop event is:
/// the handle type lives above this assembly and every subscriber re-reads the
/// live list.
/// </summary>
public record OverlayNodeListChangedEvent : IEvent;

/// <summary>
/// Published when the set of ADOPTED world objects changes (a BG/layout object
/// taken into the scene, or given back to the map). Payload-free for the same
/// reason the prop event is: the handle type lives above this assembly and
/// every subscriber re-reads the live list.
/// </summary>
public record WorldObjectListChangedEvent : IEvent;

/// <summary>
/// Published when the virtual-camera list changes (camera created, destroyed,
/// or the live camera switched). Payload-free: subscribers re-read the list.
/// </summary>
public record CameraListChangedEvent : IEvent;

#endregion

#region Service Events


#endregion
