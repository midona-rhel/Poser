using System.Numerics;

namespace Poser.UI;

public record struct SliderStyle
{
    public Display? Display;
    public Sizing? Width;
    public Sizing? MinWidth;
    public Sizing? MaxWidth;
    public Sizing? Height;
    public Sizing? MinHeight;
    public Sizing? MaxHeight;
    public Spacing? Margin;
    public Vector4? BackgroundColor;
    public Vector4? GrabColor;
    public Vector4? GrabActiveColor;
    public Vector4? Color;
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Width = Width, Height = Height,
        MinWidth = MinWidth, MaxWidth = MaxWidth, MinHeight = MinHeight, MaxHeight = MaxHeight,
        Margin = Margin,
        BackgroundColor = BackgroundColor, Color = Color, Opacity = Opacity,
    };

    public static SliderStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Width = e.Width, Height = e.Height,
        MinWidth = e.MinWidth, MaxWidth = e.MaxWidth, MinHeight = e.MinHeight, MaxHeight = e.MaxHeight,
        Margin = e.Margin,
        BackgroundColor = e.BackgroundColor, Color = e.Color, Opacity = e.Opacity,
    };

    public SliderStyle MergedWith(in SliderStyle o) => new()
    {
        Display = o.Display ?? Display,
        Width = o.Width ?? Width, Height = o.Height ?? Height,
        MinWidth = o.MinWidth ?? MinWidth, MaxWidth = o.MaxWidth ?? MaxWidth,
        MinHeight = o.MinHeight ?? MinHeight, MaxHeight = o.MaxHeight ?? MaxHeight,
        Margin = o.Margin ?? Margin,
        BackgroundColor = o.BackgroundColor ?? BackgroundColor,
        GrabColor = o.GrabColor ?? GrabColor,
        GrabActiveColor = o.GrabActiveColor ?? GrabActiveColor,
        Color = o.Color ?? Color, Opacity = o.Opacity ?? Opacity,
    };
}
