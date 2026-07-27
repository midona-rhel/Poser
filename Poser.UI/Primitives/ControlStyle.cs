using System;

namespace Poser.UI;

internal enum UiWidthKind
{
    Unspecified,
    Content,
    Fill,
    Fixed,
}

public readonly record struct UiWidth
{
    private UiWidth(UiWidthKind kind, float value = 0f)
    {
        Kind = kind;
        Value = value;
    }

    internal UiWidthKind Kind { get; }
    internal float Value { get; }

    public static UiWidth Content => new(UiWidthKind.Content);
    public static UiWidth Fill => new(UiWidthKind.Fill);
    public static UiWidth Fixed(float width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        return new(UiWidthKind.Fixed, width);
    }
}

internal enum UiHeightKind
{
    Natural,
    Workspace,
    Comfortable,
    Fixed,
}

public readonly record struct UiHeight
{
    private UiHeight(UiHeightKind kind, float value = 0f)
    {
        Kind = kind;
        Value = value;
    }

    internal UiHeightKind Kind { get; }
    internal float Value { get; }

    public static UiHeight Workspace => new(UiHeightKind.Workspace);
    public static UiHeight Comfortable => new(UiHeightKind.Comfortable);
    public static UiHeight Fixed(float height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new(UiHeightKind.Fixed, height);
    }
}

public readonly record struct ControlStyle
{
    public UiWidth Width { get; init; }
    public UiHeight Height { get; init; }
    public bool Primary { get; init; }
    public bool Bare { get; init; }

    public static ControlStyle Workspace => new() { Height = UiHeight.Workspace };
    public static ControlStyle Comfortable => new() { Height = UiHeight.Comfortable };
    public static ControlStyle Square(float side) => new()
    {
        Width = UiWidth.Fixed(side),
        Height = UiHeight.Fixed(side),
    };
}

internal static class ControlSizing
{
    public static float Height(UiHeight height, float fallback) =>
        height.Kind switch
        {
            UiHeightKind.Workspace => Crystarium.ActiveTheme.Controls.WorkspaceHeight,
            UiHeightKind.Comfortable => Crystarium.ActiveTheme.Controls.ComfortableHeight,
            UiHeightKind.Fixed => height.Value,
            _ => fallback,
        };

    public static float Width(UiWidth width, float content, float available) =>
        width.Kind switch
        {
            UiWidthKind.Fill => available,
            UiWidthKind.Fixed => width.Value,
            _ => content,
        };

    public static bool IsWorkspace(UiHeight height) =>
        height.Kind == UiHeightKind.Workspace;
}
