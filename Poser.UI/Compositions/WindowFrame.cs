using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>A frame slot in screen space.</summary>
public readonly record struct WindowFrameRect(Vector2 Min, Vector2 Max)
{
    public Vector2 Size => Max - Min;
}

/// <summary>
/// What <see cref="Crystarium.WindowFrame"/> drew and what it left to the
/// caller: the two bars are already painted, the rail band and the body are
/// empty rectangles their owner fills.
/// </summary>
public readonly record struct WindowFrameRects
{
    /// <summary>The title band, rule included.</summary>
    public WindowFrameRect TitleBar { get; init; }

    /// <summary>The band under the title bar, its bottom rule INCLUDED. Empty
    /// when there is no band.</summary>
    public WindowFrameRect Band { get; init; }

    /// <summary>The rail's raised band, its 1px rule EXCLUDED — the rule is
    /// the body's left edge, not the rail's content. Empty when there is no
    /// rail.</summary>
    public WindowFrameRect Rail { get; init; }

    /// <summary>Everything between the two bars and right of the rail rule.
    /// </summary>
    public WindowFrameRect Body { get; init; }

    /// <summary>The footer band, rule included. Empty when there is no footer.
    /// </summary>
    public WindowFrameRect Footer { get; init; }
}

/// <summary>
/// The frame's slots. A rail is its WIDTH (rule included; 0 is no rail and no
/// rule) and a footer is its ACTIONS: both slots vanish when unstated, and the
/// body takes the space back.
/// </summary>
public readonly record struct WindowFrameProps
{
    public string Title { get; init; }

    /// <summary>Unstated draws no close affordance.</summary>
    public Action? OnClose { get; init; }

    public string? CloseHelp { get; init; }

    /// <summary>Logical rail width, the 1px rule INCLUDED; 0 is no rail.
    /// </summary>
    public float RailWidth { get; init; }

    /// <summary>Logical height of a band between the title bar and the body —
    /// the file surface's navigation row; 0 is no band. The frame reserves it
    /// and rules its bottom edge full width; the caller fills the rect.
    /// </summary>
    public float BandHeight { get; init; }

    /// <summary>The host window already painted the glass — which
    /// <see cref="Crystarium.FloatingSurface.Window"/> does for every
    /// window it hosts — so the frame must not paint a second shadow over the
    /// first. A surface on a bare host leaves this unstated.</summary>
    public bool HostPaintsChrome { get; init; }

    /// <summary>The footer's left cluster. Stating either cluster is what
    /// makes the footer band exist.</summary>
    public Action<Crystarium.ActionBarScope>? FooterLeft { get; init; }

    /// <summary>The footer's right cluster.</summary>
    public Action<Crystarium.ActionBarScope>? FooterRight { get; init; }
}

public static partial class Crystarium
{
    /// <summary>
    /// THE WINDOW FRAME, and there is one. Every floating Poser window is this
    /// frame told different slots: the glass chrome, the title bar with its
    /// close affordance, an optional band under it, an optional left rail, the
    /// body, and the footer band.
    ///
    /// <para>THE ROTATED-H IS THE GEOMETRY: full-width rules under the title
    /// bar and over the footer, bridged by the rail's 1px vertical rule. The
    /// rules run WINDOW EDGE to WINDOW EDGE — past the header inset the bars
    /// pad their items to — so the frame draws them itself rather than letting
    /// <see cref="ActionBar"/> draw them at its own narrower box.</para>
    ///
    /// <para>The frame paints chrome and bars only. The rail band and the body
    /// come back as rectangles: their owner fills them, and owns any scrolling
    /// and any content inset inside them.</para>
    /// </summary>
    public static WindowFrameRects WindowFrame(
        string id,
        Vector2 min,
        Vector2 size,
        in WindowFrameProps props)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        float barHeight = theme.Floating.ModalBarHeight * scale;
        float inset = theme.Floating.HeaderInset * scale;
        float rule = MathF.Max(1f, scale);
        bool hasFooter = props.FooterLeft is not null || props.FooterRight is not null;
        float bandHeight = props.BandHeight * scale;
        float titleBottom = min.Y + barHeight;
        float bodyTop = titleBottom + bandHeight;
        float bodyBottom = hasFooter ? max.Y - barHeight : max.Y;

        if (!props.HostPaintsChrome)
            FloatingSurface.DrawChrome(drawList, min, max, theme.Radii.Window);

        ControlPaint.Separator(
            drawList,
            new Vector2(min.X, titleBottom - rule),
            max.X,
            scale,
            theme.FormSeparator);
        // The band's own closing rule. Full width like every other rule the
        // frame draws, so the chrome above and the browsing surface below read
        // as two segments.
        if (bandHeight > 0f)
            ControlPaint.Separator(
                drawList,
                new Vector2(min.X, bodyTop - rule),
                max.X,
                scale,
                theme.FormSeparator);
        // Locals: an `in` parameter cannot be captured by the bar's callbacks.
        string title = props.Title;
        var onClose = props.OnClose;
        string? closeHelp = props.CloseHelp;
        ActionBar(
            $"{id}-header",
            new Vector2(min.X + inset, min.Y),
            new Vector2(size.X - inset * 2f, barHeight),
            left => left.Label(title),
            onClose is null
                ? null
                : right => right.Icon(TablerIcon.X, onClose, closeHelp),
            ActionBarSeparator.None);

        var railRect = default(WindowFrameRect);
        float bodyLeft = min.X;
        if (props.RailWidth > 0f)
        {
            float railWidth = props.RailWidth * scale;
            railRect = new WindowFrameRect(
                new Vector2(min.X, bodyTop),
                new Vector2(min.X + railWidth - rule, bodyBottom));
            drawList.AddRectFilled(
                railRect.Min,
                railRect.Max,
                ImGui.ColorConvertFloat4ToU32(theme.Chrome.RailFill));
            // The H's bridge: it belongs to the rail, so a frame without a
            // rail has none.
            drawList.AddRectFilled(
                new Vector2(railRect.Max.X, bodyTop),
                new Vector2(railRect.Max.X + rule, bodyBottom),
                ImGui.ColorConvertFloat4ToU32(theme.FormSeparator));
            bodyLeft = min.X + railWidth;
        }

        var footerRect = default(WindowFrameRect);
        if (hasFooter)
        {
            footerRect = new WindowFrameRect(
                new Vector2(min.X, bodyBottom), max);
            drawList.AddRectFilled(
                footerRect.Min,
                footerRect.Max,
                ImGui.ColorConvertFloat4ToU32(theme.Chrome.ModalFooter),
                theme.Radii.Window * scale,
                ImDrawFlags.RoundCornersBottom);
            ControlPaint.Separator(
                drawList,
                new Vector2(min.X, bodyBottom),
                max.X,
                scale,
                theme.FormSeparator);
            ActionBar(
                $"{id}-footer",
                new Vector2(min.X + inset, bodyBottom),
                new Vector2(size.X - inset * 2f, barHeight),
                props.FooterLeft ?? (static _ => { }),
                props.FooterRight,
                ActionBarSeparator.None);
        }

        return new WindowFrameRects
        {
            TitleBar = new WindowFrameRect(
                min, new Vector2(max.X, titleBottom)),
            Band = bandHeight > 0f
                ? new WindowFrameRect(
                    new Vector2(min.X, titleBottom),
                    new Vector2(max.X, bodyTop))
                : default,
            Rail = railRect,
            Body = new WindowFrameRect(
                new Vector2(bodyLeft, bodyTop),
                new Vector2(max.X, bodyBottom)),
            Footer = footerRect,
        };
    }
}
