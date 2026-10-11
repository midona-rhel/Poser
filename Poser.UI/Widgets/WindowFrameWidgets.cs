using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.ActionBarWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.WindowMovement;

namespace Poser.UI.Widgets;

public static class WindowFrameWidgets
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
        float footerTop = hasFooter ? max.Y - barHeight : max.Y;
        float bottomBand = props.BottomBandHeight * scale;
        float bodyBottom = footerTop - bottomBand;

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
        var headerRight = props.HeaderRight;
        bool customTitle = props.TitleContent is not null;
        ActionBar(
            $"{id}-header",
            new Vector2(min.X + inset, min.Y),
            new Vector2(size.X - inset * 2f, barHeight),
            customTitle ? static _ => { } : left => left.Label(title),
            onClose is null && headerRight is null
                ? null
                : right =>
                {
                    headerRight?.Invoke(right);
                    if (onClose is not null)
                        right.Icon(TablerIcon.X, onClose, closeHelp);
                },
            ActionBarSeparator.None);
        props.TitleContent?.Invoke(new WindowFrameRect(
            min, new Vector2(max.X, titleBottom)));
        WindowTitleDrag(min, new Vector2(max.X, titleBottom));

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

        // The bottom band's own opening rule — the mirror of the top band's
        // closing one, full width like every rule the frame draws.
        if (bottomBand > 0f)
            ControlPaint.Separator(
                drawList,
                new Vector2(min.X, bodyBottom),
                MathF.Max(min.X,
                    max.X - props.BottomBandRightInset * scale),
                scale,
                theme.FormSeparator);

        var footerRect = default(WindowFrameRect);
        if (hasFooter)
        {
            footerRect = new WindowFrameRect(
                new Vector2(min.X, footerTop), max);
            drawList.AddRectFilled(
                footerRect.Min,
                footerRect.Max,
                ImGui.ColorConvertFloat4ToU32(theme.Chrome.ModalFooter),
                theme.Radii.Window * scale,
                ImDrawFlags.RoundCornersBottom);
            ControlPaint.Separator(
                drawList,
                new Vector2(min.X, footerTop),
                max.X,
                scale,
                theme.FormSeparator);
            ActionBar(
                $"{id}-footer",
                new Vector2(min.X + inset, footerTop),
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
            BottomBand = bottomBand > 0f
                ? new WindowFrameRect(
                    new Vector2(min.X, bodyBottom),
                    new Vector2(max.X, footerTop))
                : default,
            Footer = footerRect,
        };
    }
}
