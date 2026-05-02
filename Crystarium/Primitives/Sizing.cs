namespace Poser.UI;

public enum SizingMode
{
    Fixed,
    Fill,
    Flex,
}

/// <summary>
/// Width / height value. Modes:
///   <c>Fixed(px)</c> — exact unscaled pixels.
///   <c>Fill</c>      — grow to fill remaining space.
///   <c>Flex(weight)</c> — share remaining space proportionally with sibling flex children.
/// Implicit conversion from <c>float</c> creates a fixed value.
/// </summary>
public readonly struct Sizing
{
    public readonly SizingMode Mode;
    public readonly float Value;

    private Sizing(SizingMode mode, float value)
    {
        Mode = mode;
        Value = value;
    }

    public static readonly Sizing Fill = new(SizingMode.Fill, 0);
    public static Sizing Fixed(float px) => new(SizingMode.Fixed, px);
    public static Sizing Flex(float weight) => new(SizingMode.Flex, weight);

    public static implicit operator Sizing(float px) => Fixed(px);
}
