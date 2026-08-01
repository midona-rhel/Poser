namespace Poser.UI.Reactive;

public readonly struct EdgeInsets
{
    public readonly float Left;
    public readonly float Top;
    public readonly float Right;
    public readonly float Bottom;

    public EdgeInsets(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public static EdgeInsets All(float v) => new(v, v, v, v);

    public static EdgeInsets Symmetric(float horizontal, float vertical) =>
        new(horizontal, vertical, horizontal, vertical);

    public static implicit operator EdgeInsets(float uniform) => All(uniform);

    public float Horizontal => Left + Right;

    public float Vertical => Top + Bottom;
}
