namespace Poser.UI;

public readonly struct Spacing
{
    public readonly float Top;
    public readonly float Right;
    public readonly float Bottom;
    public readonly float Left;

    public Spacing(float all)
    {
        Top = Right = Bottom = Left = all;
    }

    public Spacing(float vertical, float horizontal)
    {
        Top = Bottom = vertical;
        Right = Left = horizontal;
    }

    public Spacing(float top, float right, float bottom, float left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;

    public static implicit operator Spacing(float all) => new(all);
}
