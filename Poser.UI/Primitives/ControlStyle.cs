namespace Poser.UI;

public enum UiSizeKind
{
    Content,
    Fill,
    Workspace,
    Comfortable,
    Fixed,
}

public readonly record struct UiSize
{
    private UiSize(UiSizeKind kind, float value = 0f)
    {
        Kind = kind;
        Value = value;
    }

    internal UiSizeKind Kind { get; }
    internal float Value { get; }

    public static UiSize Content => new(UiSizeKind.Content);
    public static UiSize Fill => new(UiSizeKind.Fill);
    public static UiSize Workspace => new(UiSizeKind.Workspace);
    public static UiSize Comfortable => new(UiSizeKind.Comfortable);
    public static UiSize Fixed(float value) => new(UiSizeKind.Fixed, value);
}

public readonly record struct ControlStyle
{
    public UiSize? Size { get; init; }
    public UiSize? Width { get; init; }
    public bool Primary { get; init; }
    public bool Bare { get; init; }

    public static ControlStyle Workspace => new() { Size = UiSize.Workspace };
    public static ControlStyle Comfortable => new() { Size = UiSize.Comfortable };
}

internal static class ControlSizing
{
    public static float Height(UiSize? size, float fallback) =>
        size?.Kind switch
        {
            UiSizeKind.Workspace => Crystarium.ActiveTheme.Controls.WorkspaceHeight,
            UiSizeKind.Comfortable => Crystarium.ActiveTheme.Controls.ComfortableHeight,
            UiSizeKind.Fixed => size.Value.Value,
            _ => fallback,
        };

    public static float Width(UiSize? width, float content, float available) =>
        width?.Kind switch
        {
            UiSizeKind.Fill => available,
            UiSizeKind.Fixed => width.Value.Value,
            _ => content,
        };

    public static bool IsWorkspace(UiSize? size) =>
        size?.Kind == UiSizeKind.Workspace;
}
