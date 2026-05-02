using System.Numerics;

namespace Poser.UI;

public record struct DropdownStyle
{
    public Sizing? Width;
    public Sizing? Height;
    public Spacing? Margin;

    // Value side (left)
    public Vector4? ValueBackground;
    public Vector4? BorderColor;
    public float?   BorderRadius;
    public float?   BorderWidth;

    // Chevron side (right) — typically uses live ImGui button colors with state
    public bool?    RaisedGradient;
    public BoxShadow? BoxShadow;

    // Text
    public Vector4? Color;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public float?   Opacity;

    // Popup
    public Vector4? PopupBackground;

    public ElementStyle ToElementStyle() => new()
    {
        Width = Width, Height = Height, Margin = Margin,
        BackgroundColor = ValueBackground, BorderColor = BorderColor,
        BorderRadius = BorderRadius, BorderWidth = BorderWidth,
        RaisedGradient = RaisedGradient, BoxShadow = BoxShadow,
        Color = Color, FontFamily = FontFamily, FontSize = FontSize, Opacity = Opacity,
    };

    public static DropdownStyle From(in ElementStyle e) => new()
    {
        Width = e.Width, Height = e.Height, Margin = e.Margin,
        ValueBackground = e.BackgroundColor, BorderColor = e.BorderColor,
        BorderRadius = e.BorderRadius, BorderWidth = e.BorderWidth,
        RaisedGradient = e.RaisedGradient, BoxShadow = e.BoxShadow,
        Color = e.Color, FontFamily = e.FontFamily, FontSize = e.FontSize, Opacity = e.Opacity,
    };

    public DropdownStyle MergedWith(in DropdownStyle o) => new()
    {
        Width = o.Width ?? Width, Height = o.Height ?? Height, Margin = o.Margin ?? Margin,
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
