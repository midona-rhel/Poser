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
    public Vector4? Color;       // icon color
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Width = Size, Height = Size, Margin = Margin,
        BackgroundColor = BackgroundColor, BorderRadius = BorderRadius,
        BorderWidth = BorderWidth, BorderColor = BorderColor,
        BoxShadow = BoxShadow, RaisedGradient = RaisedGradient,
        Color = Color, Opacity = Opacity,
    };

    public static ToggleStyle From(in ElementStyle e) => new()
    {
        Size = e.Width ?? e.Height, Margin = e.Margin,
        BackgroundColor = e.BackgroundColor, BorderRadius = e.BorderRadius,
        BorderWidth = e.BorderWidth, BorderColor = e.BorderColor,
        BoxShadow = e.BoxShadow, RaisedGradient = e.RaisedGradient,
        Color = e.Color, Opacity = e.Opacity,
    };

    public ToggleStyle MergedWith(in ToggleStyle o) => new()
    {
        Size = o.Size ?? Size, Margin = o.Margin ?? Margin,
        BackgroundColor = o.BackgroundColor ?? BackgroundColor,
        BorderRadius = o.BorderRadius ?? BorderRadius,
        BorderWidth = o.BorderWidth ?? BorderWidth,
        BorderColor = o.BorderColor ?? BorderColor,
        BoxShadow = o.BoxShadow ?? BoxShadow,
        RaisedGradient = o.RaisedGradient ?? RaisedGradient,
        Color = o.Color ?? Color, Opacity = o.Opacity ?? Opacity,
    };
}
