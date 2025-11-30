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
