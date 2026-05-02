using System.Numerics;

namespace Poser.UI;

/// <summary>Minimal icon toggle: outlined glyph that brightens with state, no chrome.</summary>
public record struct IconToggleStyle
{
    public Sizing? Size;
    public Spacing? Margin;
    public Vector4? OnColor;
    public Vector4? OffColor;
    public Vector4? HoverColor;
    public Vector4? OutlineColor;
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Width = Size, Height = Size, Margin = Margin,
        // OnColor/OffColor/HoverColor don't map to ElementStyle — use Color for OnColor as default
        Color = OnColor, Opacity = Opacity,
    };

    public static IconToggleStyle From(in ElementStyle e) => new()
    {
        Size = e.Width ?? e.Height, Margin = e.Margin,
        OnColor = e.Color, Opacity = e.Opacity,
    };

    public IconToggleStyle MergedWith(in IconToggleStyle o) => new()
    {
        Size = o.Size ?? Size, Margin = o.Margin ?? Margin,
        OnColor = o.OnColor ?? OnColor,
        OffColor = o.OffColor ?? OffColor,
        HoverColor = o.HoverColor ?? HoverColor,
        OutlineColor = o.OutlineColor ?? OutlineColor,
        Opacity = o.Opacity ?? Opacity,
    };
}
