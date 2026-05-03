using System.Numerics;

namespace Poser.UI;

public enum GradientDirection
{
    ToBottom,    // top → bottom (default for top-to-bottom feel)
    ToTop,
    ToRight,
    ToLeft,
    ToBottomRight,
    ToTopLeft,
}

/// <summary>
/// Linear gradient between two colors. CSS-shaped.
/// </summary>
public readonly struct Gradient
{
    public readonly GradientDirection Direction;
    public readonly Vector4 Start;
    public readonly Vector4 End;

    public Gradient(Vector4 start, Vector4 end, GradientDirection direction = GradientDirection.ToBottom)
    {
        Start = start;
        End = end;
        Direction = direction;
    }
}
