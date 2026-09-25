using System.Collections.Generic;
using Poser.Core;

namespace Poser.Entities;

/// <summary>
/// Type of entity for UI display purposes.
/// </summary>
public enum EntityType
{
    /// <summary>Generic entity.</summary>
    Generic,
    /// <summary>Player character.</summary>
    Player,
    /// <summary>NPC (battle or event).</summary>
    Npc,
    /// <summary>Companion (minion, mount, pet).</summary>
    Companion,
    /// <summary>Skeleton root.</summary>
    Skeleton,
    /// <summary>Individual bone.</summary>
    Bone,
    /// <summary>Virtual bone (calculated pivot point for bone groups).</summary>
    VirtualBone,
    /// <summary>User-created pivot point for custom orbit centers.</summary>
    PivotPoint,
}

public interface IEntity
{
    EntityId Id { get; }
    string Name { get; set; }
    Transform Transform { get; set; }

    IEntity? Parent { get; }
    IReadOnlyCollection<IEntity> Children { get; }

    bool IsVisible { get; set; }
    bool IsSelected { get; set; }

    /// <summary>
    /// Whether this entity can be collapsed in the UI (has meaningful children).
    /// </summary>
    bool IsCollapsible { get; }

    /// <summary>
    /// Whether this entity is currently collapsed in the UI.
    /// </summary>
    bool IsCollapsed { get; set; }

    /// <summary>
    /// The type of this entity for UI display.
    /// </summary>
    EntityType EntityType { get; }

    void AttachChild(IEntity child);
    void DetachChild(IEntity child);

    void OnAttached();
    void OnDetached();
    void OnSelected();
    void OnDeselected();
}
