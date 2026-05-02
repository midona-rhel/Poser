using System.Numerics;

namespace Poser.UI;

/// <summary>
/// CSS-shaped box-shadow. Offset in unscaled pixels; Blur > 0 renders a soft drop-shadow,
/// Blur = 0 renders a hard offset shadow. Color includes alpha.
/// </summary>
public readonly struct BoxShadow
{
    public readonly float OffsetX;
    public readonly float OffsetY;
    public readonly float Blur;
    public readonly Vector4 Color;

    public BoxShadow(float offsetX, float offsetY, float blur, Vector4 color)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        Blur = blur;
        Color = color;
    }

    /// <summary>Soft drop-shadow matching the legacy DrawControlShadow look.</summary>
    public static BoxShadow Soft(float blur = 4f, float opacity = 0.20f)
        => new(1f, 1f, blur, new Vector4(0f, 0f, 0f, opacity));

    /// <summary>Heavier window-style shadow.</summary>
    public static BoxShadow Window(float blur = 8f, float opacity = 0.50f)
        => new(0f, 0f, blur, new Vector4(0f, 0f, 0f, opacity));
}
