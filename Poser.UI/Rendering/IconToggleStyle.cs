using System.Numerics;

namespace Poser.UI;

/// <summary>Minimal icon toggle: outlined glyph that brightens with state, no chrome.</summary>
public record struct IconToggleStyle
{
    public Display? Display;
    public Sizing? Size;
    public Sizing? MinSize;
    public Sizing? MaxSize;
    public Spacing? Margin;
    public Vector4? OnColor;
    public Vector4? OffColor;
    public Vector4? HoverColor;
    public Vector4? OutlineColor;
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Width = Size, Height = Size,
        MinWidth = MinSize, MaxWidth = MaxSize, MinHeight = MinSize, MaxHeight = MaxSize,
        Margin = Margin,
        Color = OnColor, Opacity = Opacity,
    };

    public static IconToggleStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Size = e.Width ?? e.Height,
        MinSize = e.MinWidth ?? e.MinHeight,
        MaxSize = e.MaxWidth ?? e.MaxHeight,
        Margin = e.Margin,
        OnColor = e.Color, Opacity = e.Opacity,
    };

    public IconToggleStyle MergedWith(in IconToggleStyle o) => new()
    {
        Display = o.Display ?? Display,
        Size = o.Size ?? Size, MinSize = o.MinSize ?? MinSize, MaxSize = o.MaxSize ?? MaxSize,
        Margin = o.Margin ?? Margin,
        OnColor = o.OnColor ?? OnColor,
        OffColor = o.OffColor ?? OffColor,
        HoverColor = o.HoverColor ?? HoverColor,
        OutlineColor = o.OutlineColor ?? OutlineColor,
        Opacity = o.Opacity ?? Opacity,
    };
}
