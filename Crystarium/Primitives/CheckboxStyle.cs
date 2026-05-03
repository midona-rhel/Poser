using System.Numerics;

namespace Poser.UI;

public record struct CheckboxStyle
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
    public Vector4? CheckmarkColor;     // fills the icon (default: white)
    public Vector4? CheckmarkOutline;   // outline color (default: black)
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Width = Size, Height = Size, Margin = Margin,
        BackgroundColor = BackgroundColor, BorderRadius = BorderRadius,
        BorderWidth = BorderWidth, BorderColor = BorderColor,
        BoxShadow = BoxShadow, Color = CheckmarkColor, Opacity = Opacity,
    };

    public static CheckboxStyle From(in ElementStyle e) => new()
    {
        Size = e.Width ?? e.Height, Margin = e.Margin,
        BackgroundColor = e.BackgroundColor, BorderRadius = e.BorderRadius,
        BorderWidth = e.BorderWidth, BorderColor = e.BorderColor,
        BoxShadow = e.BoxShadow, CheckmarkColor = e.Color, Opacity = e.Opacity,
    };

    public CheckboxStyle MergedWith(in CheckboxStyle o) => new()
    {
        Size = o.Size ?? Size, Margin = o.Margin ?? Margin,
        BackgroundColor = o.BackgroundColor ?? BackgroundColor,
        BorderRadius = o.BorderRadius ?? BorderRadius,
        BorderWidth = o.BorderWidth ?? BorderWidth,
        BorderColor = o.BorderColor ?? BorderColor,
        BoxShadow = o.BoxShadow ?? BoxShadow,
        CheckmarkColor = o.CheckmarkColor ?? CheckmarkColor,
        CheckmarkOutline = o.CheckmarkOutline ?? CheckmarkOutline,
        Opacity = o.Opacity ?? Opacity,
    };
}
