using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

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

    /// <summary>The smallest width <see cref="Fixed"/> accepts, and what a
    /// region with no room left resolves to.</summary>
    public const float Minimum = 1f;

    public static UiWidth Content => new(UiWidthKind.Content);
    public static UiWidth Fill => new(UiWidthKind.Fill);

    /// <summary>An EXACT width, stated by a caller that knows it. Non-positive
    /// is a programming error here — derived widths go through
    /// <see cref="Region"/>.</summary>
    public static UiWidth Fixed(float width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        return new(UiWidthKind.Fixed, width);
    }

    /// <summary>
    /// The width a layout REGION offers a control — a span DERIVED by
    /// subtraction (a row's control column less its action strip, a pane less
    /// its gutter) rather than stated outright. A region that cannot state a
    /// positive width offers <see cref="Minimum"/>, so the control collapses
    /// to a hairline it can never paint over its neighbours with.
    /// <para>Derived spans go through this factory and NEVER through
    /// <see cref="Fixed"/>: a narrow pane, a collapsed child or a first-frame
    /// measure drives such a span to zero routinely, and <c>Fixed</c> answers
    /// that by throwing — out of layout math, mid-frame, which takes the whole
    /// window down with it rather than one row.</para>
    /// </summary>
    public static UiWidth Region(float width) =>
        new(UiWidthKind.Fixed, width > Minimum ? width : Minimum);
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

    /// <summary>CSS <c>max-width</c>, in logical units: caps whatever
    /// <see cref="Width"/> resolves to — intrinsic, Fixed, Fill, or a
    /// control's own usability floor. A layout track that must contain its
    /// control (a <see cref="Crystarium.FormPairCell"/> half) states its
    /// span here; containment outranks any minimum the control would
    /// otherwise enforce.</summary>
    public float? MaxWidth { get; init; }
    /// <summary>The LATCHED on-state. An icon button wearing it draws the
    /// accent wash and its paired 1px accent edge — the app's one "this is
    /// on" convention — and is otherwise sized and hit-tested exactly as the
    /// same button off. Typed text buttons ignore it.</summary>
    public bool Selected { get; init; }

    public static ControlStyle Workspace => new() { Height = UiHeight.Workspace };
    public static ControlStyle Comfortable => new() { Height = UiHeight.Comfortable };
    public static ControlStyle Square(float side) => new()
    {
        Width = UiWidth.Fixed(side),
        Height = UiHeight.Fixed(side),
    };
}

/// <summary>
/// One control's resolved box: the frame scale, the logical (unscaled)
/// span the style asked for, and the same span in pixels. Produced by
/// <see cref="ControlSizing.Resolve"/> so every control derives its size
/// through one preamble instead of re-deriving scale, available width,
/// and the two <see cref="ControlSizing"/> lookups by hand.
/// </summary>
internal readonly struct ResolvedControl
{
    public ResolvedControl(
        float scale,
        float logicalWidth,
        float logicalHeight)
    {
        Scale = scale;
        LogicalWidth = logicalWidth;
        LogicalHeight = logicalHeight;
    }

    /// <summary>ImGuiHelpers.GlobalScale at resolution time.</summary>
    public readonly float Scale;
    public readonly float LogicalWidth;
    public readonly float LogicalHeight;

    public float Width => LogicalWidth * Scale;
    public float Height => LogicalHeight * Scale;
    public Vector2 Size => new(Width, Height);
}

internal static class ControlSizing
{
    /// <summary>
    /// The shared sizing preamble: scale, the available region in logical
    /// units, and the width/height the style resolves to.
    /// <paramref name="logicalContentWidth"/> is the control's intrinsic
    /// (content) width in unscaled units — what
    /// <see cref="UiWidthKind.Content"/> means for that control.
    /// </summary>
    public static ResolvedControl Resolve(
        in ControlStyle style,
        float logicalContentWidth,
        float fallbackHeight)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float availableLogical = ImGui.GetContentRegionAvail().X / scale;
        return new ResolvedControl(
            scale,
            Width(style, logicalContentWidth, availableLogical),
            Height(style.Height, fallbackHeight));
    }

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

    /// <summary>The style-aware width: what <see cref="Width(UiWidth,
    /// float, float)"/> resolves, held under the style's
    /// <see cref="ControlStyle.MaxWidth"/>. Controls resolve through THIS
    /// overload so a track cap governs every width kind.</summary>
    public static float Width(
        in ControlStyle style, float content, float available) =>
        Cap(Width(style.Width, content, available), style.MaxWidth);

    /// <summary>The <see cref="ControlStyle.MaxWidth"/> clamp, alone — for
    /// a control that raises its resolved width afterwards (a usability
    /// floor) and must re-assert containment on the result.</summary>
    public static float Cap(float width, float? maxWidth) =>
        maxWidth is { } max ? MathF.Min(width, max) : width;

    public static bool IsWorkspace(UiHeight height) =>
        height.Kind == UiHeightKind.Workspace;
}
