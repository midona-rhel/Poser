using System;

namespace Poser.UI;

/// <summary>
/// Per-element attributes (analog of HTML attributes). Style cascade is in <see cref="Style"/>.
/// </summary>
public record struct ElementProps
{
    /// <summary>ImGui ID and (future) CSS #id selector. Required for any element that owns input.</summary>
    public string? Id;

    /// <summary>Space-separated class names (e.g. "btn primary active"). Looked up in the global Stylesheet.</summary>
    public string? ClassName;

    /// <summary>Setting this makes the element interactive (hit-tested for :hover/:active states).</summary>
    public Action? OnClick;

    /// <summary>Right-click handler. Setting this also makes the element interactive.</summary>
    public Action? OnContextMenu;

    /// <summary>If true, suppresses OnClick, applies :disabled state, dims content via opacity.</summary>
    public bool? Disabled;

    /// <summary>Inline style — highest priority in the cascade.</summary>
    public ElementStyle Style;

    /// <summary>Optional tooltip shown when the element is hovered.</summary>
    public string? Tooltip;
}
