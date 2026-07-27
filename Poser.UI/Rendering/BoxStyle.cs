using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Chrome-only style used by <see cref="BoxRenderer"/> at explicit screen
/// rectangles without cursor manipulation.
/// </summary>
public record struct BoxStyle
{
    public Vector4? BackgroundColor;
    public Gradient? BackgroundGradient;
    public IImageSource? BackgroundImage;
    public ImageFit? BackgroundImageFit;
    public SvgDocument? BackgroundSvg;
    public Vector4? BorderColor;
    public float BorderWidth;
    public float BorderRadius;

    /// <summary>
    /// Per-side border colors (CSS border-top-color etc.). Any side left null falls back
    /// to <see cref="BorderColor"/>. Needed for the picto glass border trio
    /// (bright top / mid sides / dark bottom); corners split at 45° between sides.
    /// </summary>
    public Vector4? BorderTopColor;
    public Vector4? BorderRightColor;
    public Vector4? BorderBottomColor;
    public Vector4? BorderLeftColor;
    public BoxShadow? BoxShadow;
    public BoxShadow[]? BoxShadows;
    public Outline? Outline;

    /// <summary>
    /// If true, paints a top-highlight + bottom-shadow gradient inside the box
    /// (matches the legacy "raised" PoserButton look). Skipped when caller wants flat.
    /// </summary>
    public bool RaisedGradient;
}
