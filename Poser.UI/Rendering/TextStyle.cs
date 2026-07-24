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
    public TextOverflow? TextOverflow;
    public WhiteSpace?   WhiteSpace;
    public float?        LineHeight;
    public float?        LetterSpacing;
    public TextShadow?   TextShadow;
    public Sizing? Width;     // optional, for text-overflow / wrap calculations
    public Sizing? MaxWidth;
    public Spacing? Margin;

    public ElementStyle ToElementStyle() => new()
    {
        Display = Display,
        Color = Color, Opacity = Opacity,
        FontFamily = FontFamily, FontSize = FontSize,
        TextAlign = TextAlign,
        TextOverflow = TextOverflow, WhiteSpace = WhiteSpace,
        LineHeight = LineHeight, LetterSpacing = LetterSpacing,
        TextShadow = TextShadow,
        Width = Width, MaxWidth = MaxWidth,
        Margin = Margin,
    };

    public static TextStyle From(in ElementStyle e) => new()
    {
        Display = e.Display,
        Color = e.Color, Opacity = e.Opacity,
        FontFamily = e.FontFamily, FontSize = e.FontSize,
        TextAlign = e.TextAlign,
        TextOverflow = e.TextOverflow, WhiteSpace = e.WhiteSpace,
        LineHeight = e.LineHeight, LetterSpacing = e.LetterSpacing,
        TextShadow = e.TextShadow,
        Width = e.Width, MaxWidth = e.MaxWidth,
        Margin = e.Margin,
    };

    public TextStyle MergedWith(in TextStyle o) => new()
    {
        Display = o.Display ?? Display,
        Color = o.Color ?? Color, Opacity = o.Opacity ?? Opacity,
        FontFamily = o.FontFamily ?? FontFamily, FontSize = o.FontSize ?? FontSize,
        TextAlign = o.TextAlign ?? TextAlign,
        TextOverflow = o.TextOverflow ?? TextOverflow, WhiteSpace = o.WhiteSpace ?? WhiteSpace,
        LineHeight = o.LineHeight ?? LineHeight, LetterSpacing = o.LetterSpacing ?? LetterSpacing,
        TextShadow = o.TextShadow ?? TextShadow,
        Width = o.Width ?? Width, MaxWidth = o.MaxWidth ?? MaxWidth,
        Margin = o.Margin ?? Margin,
    };
}
