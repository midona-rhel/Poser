using System.Numerics;

namespace Poser.UI;

public record struct ScrubberStyle
{
    public Display? Display;
    public Sizing? Width;
    public Sizing? MinWidth;
    public Sizing? MaxWidth;
    public Sizing? Height;
    public Sizing? MinHeight;
    public Sizing? MaxHeight;
    public Spacing? Margin;

    // Track
    public Vector4? TrackColor;
    public float?   TrackHeight;
    public Vector4? TickColor;

    // Thumb
    public Vector4? ThumbColor;
    public float?   ThumbWidth;
    public float?   ThumbBorderRadius;
    public Vector4? ThumbBorderColor;
    public float?   ThumbBorderWidth;
    public BoxShadow? ThumbShadow;
    public bool?    ThumbRaisedGradient;

    // Value text
    public Vector4? Color;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public float?   Opacity;

    public ElementStyle ToElementStyle() => new()
    {
        Width = Width, Height = Height, Margin = Margin,
        // Other custom track/thumb fields don't map cleanly; ElementStyle keeps the ones it can
        Color = Color, FontFamily = FontFamily, FontSize = FontSize, Opacity = Opacity,
    };

    public static ScrubberStyle From(in ElementStyle e) => new()
    {
        Width = e.Width, Height = e.Height, Margin = e.Margin,
        Color = e.Color, FontFamily = e.FontFamily, FontSize = e.FontSize, Opacity = e.Opacity,
    };

    public ScrubberStyle MergedWith(in ScrubberStyle o) => new()
    {
        Width = o.Width ?? Width, Height = o.Height ?? Height, Margin = o.Margin ?? Margin,
        TrackColor = o.TrackColor ?? TrackColor,
        TrackHeight = o.TrackHeight ?? TrackHeight,
        TickColor = o.TickColor ?? TickColor,
        ThumbColor = o.ThumbColor ?? ThumbColor,
        ThumbWidth = o.ThumbWidth ?? ThumbWidth,
        ThumbBorderRadius = o.ThumbBorderRadius ?? ThumbBorderRadius,
        ThumbBorderColor = o.ThumbBorderColor ?? ThumbBorderColor,
        ThumbBorderWidth = o.ThumbBorderWidth ?? ThumbBorderWidth,
        ThumbShadow = o.ThumbShadow ?? ThumbShadow,
        ThumbRaisedGradient = o.ThumbRaisedGradient ?? ThumbRaisedGradient,
        Color = o.Color ?? Color, FontFamily = o.FontFamily ?? FontFamily,
        FontSize = o.FontSize ?? FontSize, Opacity = o.Opacity ?? Opacity,
    };
}
