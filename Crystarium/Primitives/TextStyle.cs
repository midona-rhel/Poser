using System.Numerics;

namespace Poser.UI;

/// <summary>Style for <see cref="Crystarium.Text"/>. Text isn't interactive — no box chrome fields.</summary>
public record struct TextStyle
{
    public Display? Display;
    public Vector4? Color;
    public float?   Opacity;
    public FontFamily? FontFamily;
    public float?   FontSize;
    public TextAlign? TextAlign;
    public Spacing? Margin;

    public ElementStyle ToElementStyle() => new()
    {
        Color = Color, Opacity = Opacity,
        FontFamily = FontFamily, FontSize = FontSize,
        TextAlign = TextAlign, Margin = Margin,
    };

    public static TextStyle From(in ElementStyle e) => new()
    {
        Color = e.Color, Opacity = e.Opacity,
        FontFamily = e.FontFamily, FontSize = e.FontSize,
        TextAlign = e.TextAlign, Margin = e.Margin,
    };

    public TextStyle MergedWith(in TextStyle o) => new()
    {
        Color = o.Color ?? Color, Opacity = o.Opacity ?? Opacity,
        FontFamily = o.FontFamily ?? FontFamily, FontSize = o.FontSize ?? FontSize,
        TextAlign = o.TextAlign ?? TextAlign, Margin = o.Margin ?? Margin,
    };
}
