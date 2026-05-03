using System.Numerics;

namespace Poser.UI;

public record struct TextInputStyle
{
    public Display? Display;
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
    public Vector4? Color;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Width = Width, Height = Height,
        MinWidth = MinWidth, MaxWidth = MaxWidth, MinHeight = MinHeight, MaxHeight = MaxHeight,
        Margin = Margin, Padding = Padding,
        BackgroundColor = BackgroundColor, BorderRadius = BorderRadius,
        BorderWidth = BorderWidth, BorderColor = BorderColor,
        Color = Color, FontFamily = FontFamily, FontSize = FontSize, Opacity = Opacity,
    };

    public static TextInputStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Width = e.Width, Height = e.Height,
        MinWidth = e.MinWidth, MaxWidth = e.MaxWidth, MinHeight = e.MinHeight, MaxHeight = e.MaxHeight,
        Margin = e.Margin, Padding = e.Padding,
        BackgroundColor = e.BackgroundColor, BorderRadius = e.BorderRadius,
        BorderWidth = e.BorderWidth, BorderColor = e.BorderColor,
        Color = e.Color, FontFamily = e.FontFamily, FontSize = e.FontSize, Opacity = e.Opacity,
    };

    public TextInputStyle MergedWith(in TextInputStyle o) => new()
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
        Color = o.Color ?? Color, FontFamily = o.FontFamily ?? FontFamily,
        FontSize = o.FontSize ?? FontSize, Opacity = o.Opacity ?? Opacity,
    };
}
