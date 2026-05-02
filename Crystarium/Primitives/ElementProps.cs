using System;

namespace Poser.UI;

/// <summary>
/// Per-element attributes for the generic <see cref="Crystarium.Element"/>.
/// Tags use their own typed props (<see cref="ButtonProps"/> etc.) instead.
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
}
