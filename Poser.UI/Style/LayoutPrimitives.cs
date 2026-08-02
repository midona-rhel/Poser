namespace Poser.UI;

public enum UiFlow : byte
{
    Row,
    Column,
    Stack,
}

/// <summary>Justify is the main axis, Align the cross axis.</summary>
public enum UiAlign : byte
{
    Start,
    Center,
    End,
    Stretch,
}

public enum UiDimKind : byte
{
    Content,
    Fill,
    Fixed,
}

/// <summary>One axis length. A default <see cref="UiDim"/> is Content.</summary>
public readonly record struct UiDim
{
    public readonly UiDimKind Kind;
    public readonly float Value;

    private UiDim(UiDimKind kind, float value)
    {
        Kind = kind;
        Value = value;
    }

    public static UiDim Content => default;

    public static UiDim Fill => new(UiDimKind.Fill, 0f);

    public static UiDim Fixed(float value) => new(UiDimKind.Fixed, value);
}
