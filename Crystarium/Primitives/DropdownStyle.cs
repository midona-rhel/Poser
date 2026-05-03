using System.Numerics;

namespace Poser.UI;

public record struct DropdownStyle
{
    public Display? Display;
    public Sizing? Width;
    public Sizing? MinWidth;
    public Sizing? MaxWidth;
    public Sizing? Height;
    public Sizing? MinHeight;
    public Sizing? MaxHeight;
    public Spacing? Margin;

    public Vector4? ValueBackground;
    public Vector4? BorderColor;
    public float?   BorderRadius;
    public float?   BorderWidth;

    public bool?    RaisedGradient;
    public BoxShadow? BoxShadow;

    public Vector4? Color;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public float?   Opacity;

    public Vector4? PopupBackground;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Width = Width, Height = Height,
        MinWidth = MinWidth, MaxWidth = MaxWidth, MinHeight = MinHeight, MaxHeight = MaxHeight,
        Margin = Margin,
        BackgroundColor = ValueBackground, BorderColor = BorderColor,
        BorderRadius = BorderRadius, BorderWidth = BorderWidth,
        RaisedGradient = RaisedGradient, BoxShadow = BoxShadow,
        Color = Color, FontFamily = FontFamily, FontSize = FontSize, Opacity = Opacity,
    };

    public static DropdownStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Width = e.Width, Height = e.Height,
        MinWidth = e.MinWidth, MaxWidth = e.MaxWidth, MinHeight = e.MinHeight, MaxHeight = e.MaxHeight,
        Margin = e.Margin,
        ValueBackground = e.BackgroundColor, BorderColor = e.BorderColor,
        BorderRadius = e.BorderRadius, BorderWidth = e.BorderWidth,
        RaisedGradient = e.RaisedGradient, BoxShadow = e.BoxShadow,
        Color = e.Color, FontFamily = e.FontFamily, FontSize = e.FontSize, Opacity = e.Opacity,
    };

    public DropdownStyle MergedWith(in DropdownStyle o) => new()
    {
        Display = o.Display ?? Display,
        Width = o.Width ?? Width, Height = o.Height ?? Height,
        MinWidth = o.MinWidth ?? MinWidth, MaxWidth = o.MaxWidth ?? MaxWidth,
        MinHeight = o.MinHeight ?? MinHeight, MaxHeight = o.MaxHeight ?? MaxHeight,
        Margin = o.Margin ?? Margin,
        ValueBackground = o.ValueBackground ?? ValueBackground,
        BorderColor = o.BorderColor ?? BorderColor,
        BorderRadius = o.BorderRadius ?? BorderRadius,
        BorderWidth = o.BorderWidth ?? BorderWidth,
        RaisedGradient = o.RaisedGradient ?? RaisedGradient,
        BoxShadow = o.BoxShadow ?? BoxShadow,
        Color = o.Color ?? Color, FontFamily = o.FontFamily ?? FontFamily,
        FontSize = o.FontSize ?? FontSize, Opacity = o.Opacity ?? Opacity,
        PopupBackground = o.PopupBackground ?? PopupBackground,
    };
}
