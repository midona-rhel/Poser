namespace Poser.UI;

public enum SizingMode
{
    Auto,
    Fixed,
    Fill,
    Flex,
}

public readonly struct Sizing
{
    public readonly SizingMode Mode;
    public readonly float Value;

    private Sizing(SizingMode mode, float value)
    {
        Mode = mode;
        Value = value;
    }

    public static readonly Sizing Auto = new(SizingMode.Auto, 0);
    public static readonly Sizing Fill = new(SizingMode.Fill, 0);
    public static Sizing Fixed(float px) => new(SizingMode.Fixed, px);
    public static Sizing Flex(float weight) => new(SizingMode.Flex, weight);

    public static implicit operator Sizing(float px) => Fixed(px);
}
