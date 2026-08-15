using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Chrome-only style used by <see cref="BoxRenderer"/> at explicit screen
/// rectangles without cursor manipulation.
/// </summary>
public record struct BoxStyle
{
    public Vector4? BackgroundColor;
    public float BorderWidth;
    public float BorderRadius;

    /// <summary>
    /// Per-side border colors (CSS border-top-color etc.). A side left null is
    /// not stroked. Needed for the picto glass border trio (bright top / mid
    /// sides / dark bottom); corners split at 45° between sides.
    /// </summary>
    public Vector4? BorderTopColor;
    public Vector4? BorderRightColor;
    public Vector4? BorderBottomColor;
    public Vector4? BorderLeftColor;
    public BoxShadow? BoxShadow;
    public BoxShadow[]? BoxShadows;
}
