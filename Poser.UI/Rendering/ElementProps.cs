using System;

namespace Poser.UI;

/// <summary>
/// Per-element attributes for the generic <see cref="Norvrandt.Element"/>.
/// Tags expose concise semantic calls and keep low-level styling internal.
/// </summary>
public record struct ElementProps
{
    /// <summary>ImGui ID. Required for any element that owns input.</summary>
    public string? Id;

    /// <summary>Class set; combine with <c>+</c> from <see cref="Cls"/> tokens.</summary>
    public StyleClassSet Classes;

    /// <summary>Setting this makes the element interactive (hit-tested for :hover/:active).</summary>
    public Action? OnClick;

    /// <summary>Right-click handler. Setting this also makes the element interactive.</summary>
    public Action? OnContextMenu;

    /// <summary>If true, suppresses OnClick and applies :disabled.</summary>
    public bool Disabled;

    /// <summary>Inline style — highest priority in cascade.</summary>
    public ElementStyle Style;

    /// <summary>Tooltip shown on hover.</summary>
    public string? Tooltip;

    /// <summary>
    /// Returns the payload object when the user starts dragging this element.
    /// Set to non-null to make the element a drag source. Payload may be any
    /// object — receiver casts to the expected type.
    /// </summary>
    public Func<object>? OnDragStart;

    /// <summary>Receives the payload when a draggable element is dropped on this one.</summary>
    public Action<object>? OnDrop;

    /// <summary>
    /// Filter for accepted drop payloads. Return true to accept; the element
    /// shows <see cref="ElementStyle.DragHoverColor"/> when hovered with a valid
    /// payload. Default: accept all when <see cref="OnDrop"/> is set.
    /// </summary>
    public Func<object, bool>? CanAcceptDrop;
}
