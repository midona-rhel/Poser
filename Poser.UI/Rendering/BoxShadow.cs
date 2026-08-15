using System.Numerics;

namespace Poser.UI;

/// <summary>
/// CSS-shaped box-shadow. Offset in unscaled pixels; Blur > 0 renders a soft drop-shadow,
/// Blur = 0 renders a hard offset shadow. Spread expands the shadow outward (CSS spread).
/// Color includes alpha. Multiple shadows stack through <see cref="BoxStyle"/>.
/// </summary>
public readonly struct BoxShadow
{
    public readonly float OffsetX;
    public readonly float OffsetY;
    public readonly float Blur;
    public readonly float Spread;
    public readonly Vector4 Color;
    public readonly bool Inset;

    public BoxShadow(float offsetX, float offsetY, float blur, Vector4 color, float spread = 0f, bool inset = false)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        Blur = blur;
        Spread = spread;
        Color = color;
        Inset = inset;
    }
}
