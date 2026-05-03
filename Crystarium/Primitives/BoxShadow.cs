using System.Numerics;

namespace Poser.UI;

/// <summary>
/// CSS-shaped box-shadow. Offset in unscaled pixels; Blur > 0 renders a soft drop-shadow,
/// Blur = 0 renders a hard offset shadow. Spread expands the shadow outward (CSS spread).
/// Color includes alpha. Multiple shadows stack via <see cref="ElementStyle.BoxShadows"/>.
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

    /// <summary>Soft drop-shadow matching the legacy DrawControlShadow look.</summary>
    public static BoxShadow Soft(float blur = 4f, float opacity = 0.20f)
        => new(1f, 1f, blur, new Vector4(0f, 0f, 0f, opacity));

    /// <summary>Heavier window-style shadow.</summary>
    public static BoxShadow Window(float blur = 8f, float opacity = 0.50f)
        => new(0f, 0f, blur, new Vector4(0f, 0f, 0f, opacity));

    /// <summary>Outer glow — blurry, no offset, with positive spread for prominence.</summary>
    public static BoxShadow Glow(Vector4 color, float blur = 8f, float spread = 2f)
        => new(0f, 0f, blur, color, spread);

    /// <summary>Top-edge inset highlight (subtle bevel above content).</summary>
    public static BoxShadow InsetHighlight(float opacity = 0.15f)
        => new(0f, 1f, 1f, new Vector4(1f, 1f, 1f, opacity), 0f, true);
}
