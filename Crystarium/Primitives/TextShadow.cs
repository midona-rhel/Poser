using System.Numerics;

namespace Poser.UI;

/// <summary>
/// CSS text-shadow: offset + blur + color. Multiple shadows stack via array.
/// </summary>
public readonly struct TextShadow
{
    public readonly float OffsetX;
    public readonly float OffsetY;
    public readonly float Blur;
    public readonly Vector4 Color;

    public TextShadow(float offsetX, float offsetY, float blur, Vector4 color)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        Blur = blur;
        Color = color;
    }

    /// <summary>Soft glow centered behind text. Set blur > 0; offsets at 0.</summary>
    public static TextShadow Glow(Vector4 color, float blur = 4f)
        => new(0f, 0f, blur, color);
}
