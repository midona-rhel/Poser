using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;

namespace Poser.UI;

/// <summary>
/// HTML-shaped UI primitive. The single entry point for Crystarium.
///
/// Idiomatic usage:
///   Crystarium.Element(new() { ClassName = "row" }, () => {
///       Crystarium.Text("Color", new() { ClassName = "label" });
///       Crystarium.Element(new() { Style = new() { Width = Sizing.Fill } }, () => { /* ... */ });
///   });
///
/// State is automatically applied to interactive elements (those with OnClick set
/// or that come from a stateful tag like Button/Checkbox/Toggle): :hover, :active,
/// :disabled, plus tag-specific pseudos (:checked, :on, :focus, :open, :expanded).
/// </summary>
public static partial class Crystarium
{
    /// <summary>Render an element with optional children.</summary>
    public static void Element(ElementProps props, Action? children = null)
        => UI.Element.Render(props, children);

    /// <summary>Plain inline text using the cascade-inherited text color/font/size.</summary>
    public static void Text(string text, ElementProps props = default)
    {
        // If any style is requested, route through Element so the cascade applies.
        if (props.ClassName != null || props.OnClick != null || props.Disabled.HasValue ||
            !props.Style.Equals(default(ElementStyle)))
        {
            UI.Element.Render(props, () => RenderTextLine(text));
            return;
        }
        RenderTextLine(text);
    }

    private static void RenderTextLine(string text)
    {
        // Vertical centering inside row cells (when ambient height is set)
        float ambientH = AvailableHeight;
        if (ambientH > 0f)
        {
            float offsetY = (ambientH - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        }
        ImGui.Text(text);
    }

    /// <summary>Low-level: paint chrome at an explicit screen rect with no cursor manipulation.</summary>
    public static void Box(Vector2 min, Vector2 max, in BoxStyle style)
        => BoxRenderer.Draw(ImGui.GetWindowDrawList(), min, max, style);

    /// <summary>Resolved inner width of the enclosing element (for raw ImGui calls inside a div).</summary>
    public static float AvailableWidth => UI.Element._ambientWidth > 0f ? UI.Element._ambientWidth : ImGui.GetContentRegionAvail().X;

    /// <summary>Resolved inner height of the enclosing element (0 when not inside a sized container).</summary>
    public static float AvailableHeight => UI.Element._ambientHeight;

    // ---- Stylesheet façade ----

    public static class Sheet
    {
        public static void Define(string selector, ElementStyle style) => Stylesheet.Define(selector, style);
        public static void Reset() => Stylesheet.Reset();
    }
}
