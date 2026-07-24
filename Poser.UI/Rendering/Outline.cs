using System.Numerics;

namespace Poser.UI;

/// <summary>
/// CSS-shaped outline. Drawn outside the border, doesn't affect layout.
/// </summary>
public readonly struct Outline
{
    public readonly float Width;
    public readonly float Offset;
    public readonly Vector4 Color;

    public Outline(float width, Vector4 color, float offset = 0f)
    {
        Width = width;
        Offset = offset;
        Color = color;
    }
}
