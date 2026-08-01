using System;

namespace Poser.UI.Reactive;

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
public readonly struct UiDim
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

[Flags]
internal enum UiStyleFields : byte
{
    None = 0,
    Flow = 1 << 0,
    Gap = 1 << 1,
    Padding = 1 << 2,
    Margin = 1 << 3,
    Width = 1 << 4,
    Height = 1 << 5,
    Justify = 1 << 6,
    Align = 1 << 7,
}

/// <summary>
/// Sparse immutable layout description. The flags word records which fields a
/// patch actually asked for, so <see cref="Extend"/> can merge without a
/// separate patch type.
/// </summary>
public readonly struct UiStyle
{
    internal readonly UiStyleFields Set;
    public readonly UiFlow Flow;
    public readonly float Gap;
    public readonly EdgeInsets Padding;
    public readonly EdgeInsets Margin;
    public readonly UiDim Width;
    public readonly UiDim Height;
    public readonly UiAlign Justify;
    public readonly UiAlign Align;

    internal UiStyle(
        UiStyleFields set,
        UiFlow flow,
        float gap,
        EdgeInsets padding,
        EdgeInsets margin,
        UiDim width,
        UiDim height,
        UiAlign justify,
        UiAlign align)
    {
        Set = set;
        Flow = flow;
        Gap = gap;
        Padding = padding;
        Margin = margin;
        Width = width;
        Height = height;
        Justify = justify;
        Align = align;
    }

    public static UiStyle Extend(in UiStyle baseStyle, in UiStyle patch)
    {
        UiStyleFields set = patch.Set;
        return new UiStyle(
            baseStyle.Set | set,
            (set & UiStyleFields.Flow) != 0 ? patch.Flow : baseStyle.Flow,
            (set & UiStyleFields.Gap) != 0 ? patch.Gap : baseStyle.Gap,
            (set & UiStyleFields.Padding) != 0 ? patch.Padding : baseStyle.Padding,
            (set & UiStyleFields.Margin) != 0 ? patch.Margin : baseStyle.Margin,
            (set & UiStyleFields.Width) != 0 ? patch.Width : baseStyle.Width,
            (set & UiStyleFields.Height) != 0 ? patch.Height : baseStyle.Height,
            (set & UiStyleFields.Justify) != 0 ? patch.Justify : baseStyle.Justify,
            (set & UiStyleFields.Align) != 0 ? patch.Align : baseStyle.Align);
    }
}
