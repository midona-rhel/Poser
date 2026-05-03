using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Style record for <see cref="Crystarium.Button"/> and <see cref="Crystarium.IconButton"/>.
/// Only fields a button actually honors. Setting nonsensical fields (e.g. FlexDirection)
/// is a compile error.
/// </summary>
public record struct ButtonStyle
{
    public Display? Display;

    // Box
    public Sizing? Width;
    public Sizing? MinWidth;
    public Sizing? MaxWidth;
    public Sizing? Height;
    public Sizing? MinHeight;
    public Sizing? MaxHeight;
    public Spacing? Margin;
    public Spacing? Padding;
    public Vector4? BackgroundColor;
    public float?   BorderRadius;
    public float?   BorderWidth;
    public Vector4? BorderColor;
    public BoxShadow? BoxShadow;
    public bool? RaisedGradient;

    // Text (label / icon color)
    public Vector4? Color;
    public float?   Opacity;
    public FontFamily? FontFamily;
    public float?   FontSize;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Width = Width, Height = Height,
        MinWidth = MinWidth, MaxWidth = MaxWidth, MinHeight = MinHeight, MaxHeight = MaxHeight,
        Margin = Margin, Padding = Padding,
        BackgroundColor = BackgroundColor, BorderRadius = BorderRadius,
        BorderWidth = BorderWidth, BorderColor = BorderColor,
        BoxShadow = BoxShadow, RaisedGradient = RaisedGradient,
        Color = Color, Opacity = Opacity, FontFamily = FontFamily, FontSize = FontSize,
    };

    public static ButtonStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Width = e.Width, Height = e.Height,
        MinWidth = e.MinWidth, MaxWidth = e.MaxWidth, MinHeight = e.MinHeight, MaxHeight = e.MaxHeight,
        Margin = e.Margin, Padding = e.Padding,
        BackgroundColor = e.BackgroundColor, BorderRadius = e.BorderRadius,
        BorderWidth = e.BorderWidth, BorderColor = e.BorderColor,
        BoxShadow = e.BoxShadow, RaisedGradient = e.RaisedGradient,
        Color = e.Color, Opacity = e.Opacity, FontFamily = e.FontFamily, FontSize = e.FontSize,
    };

    public ButtonStyle MergedWith(in ButtonStyle o) => new()
    {
        Display = o.Display ?? Display,
        Width = o.Width ?? Width, Height = o.Height ?? Height,
        MinWidth = o.MinWidth ?? MinWidth, MaxWidth = o.MaxWidth ?? MaxWidth,
        MinHeight = o.MinHeight ?? MinHeight, MaxHeight = o.MaxHeight ?? MaxHeight,
        Margin = o.Margin ?? Margin, Padding = o.Padding ?? Padding,
        BackgroundColor = o.BackgroundColor ?? BackgroundColor,
        BorderRadius = o.BorderRadius ?? BorderRadius,
        BorderWidth = o.BorderWidth ?? BorderWidth,
        BorderColor = o.BorderColor ?? BorderColor,
        BoxShadow = o.BoxShadow ?? BoxShadow,
        RaisedGradient = o.RaisedGradient ?? RaisedGradient,
        Color = o.Color ?? Color, Opacity = o.Opacity ?? Opacity,
        FontFamily = o.FontFamily ?? FontFamily, FontSize = o.FontSize ?? FontSize,
    };
}
