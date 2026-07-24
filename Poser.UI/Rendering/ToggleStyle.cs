using System.Numerics;

namespace Poser.UI;

public record struct ToggleStyle
{
    public Display? Display;
    public Sizing? Size;
    public Sizing? MinSize;
    public Sizing? MaxSize;
    public Spacing? Margin;
    public Vector4? BackgroundColor;
    public float?   BorderRadius;
    public float?   BorderWidth;
    public Vector4? BorderColor;
    public BoxShadow? BoxShadow;
    public bool?    RaisedGradient;
    public Vector4? Color;
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Width = Size, Height = Size,
        MinWidth = MinSize, MaxWidth = MaxSize, MinHeight = MinSize, MaxHeight = MaxSize,
        Margin = Margin,
        BackgroundColor = BackgroundColor, BorderRadius = BorderRadius,
        BorderWidth = BorderWidth, BorderColor = BorderColor,
        BoxShadow = BoxShadow, RaisedGradient = RaisedGradient,
        Color = Color, Opacity = Opacity,
    };

    public static ToggleStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Size = e.Width ?? e.Height,
        MinSize = e.MinWidth ?? e.MinHeight,
        MaxSize = e.MaxWidth ?? e.MaxHeight,
        Margin = e.Margin,
        BackgroundColor = e.BackgroundColor, BorderRadius = e.BorderRadius,
        BorderWidth = e.BorderWidth, BorderColor = e.BorderColor,
        BoxShadow = e.BoxShadow, RaisedGradient = e.RaisedGradient,
        Color = e.Color, Opacity = e.Opacity,
    };

    public ToggleStyle MergedWith(in ToggleStyle o) => new()
    {
        Display = o.Display ?? Display,
        Size = o.Size ?? Size, MinSize = o.MinSize ?? MinSize, MaxSize = o.MaxSize ?? MaxSize,
        Margin = o.Margin ?? Margin,
        BackgroundColor = o.BackgroundColor ?? BackgroundColor,
        BorderRadius = o.BorderRadius ?? BorderRadius,
        BorderWidth = o.BorderWidth ?? BorderWidth,
        BorderColor = o.BorderColor ?? BorderColor,
        BoxShadow = o.BoxShadow ?? BoxShadow,
        RaisedGradient = o.RaisedGradient ?? RaisedGradient,
        Color = o.Color ?? Color, Opacity = o.Opacity ?? Opacity,
    };
}
