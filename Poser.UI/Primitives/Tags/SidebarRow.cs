using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public enum SidebarExpander { None, Collapsed, Open }

public record struct SidebarRowProps
{
    public TablerIcon Icon;
    /// <summary>
    /// Optional game-supplied icon drawn in the icon slot INSTEAD of
    /// <see cref="Icon"/>. The caller owns resolution and lifetime —
    /// Dalamud's shared textures must be re-resolved every frame and
    /// never cached — so this takes an already-resolved wrap rather than
    /// an icon id.
    /// </summary>
    public IDalamudTextureWrap? IconTexture;
    /// <summary>Right-aligned mono badge (counts, "you", "spawned").</summary>
    public string? Badge;
    public bool Selected;
    /// <summary>Left inset of the highlight pill (CSS <c>--row-inset</c>,
    /// which is the tree indent plus one pixel), unscaled px. 0 → picto
    /// default 1px. Content sits one pixel left of it, at the CSS
    /// <c>padding-left</c>.</summary>
    public float Inset;
    public SidebarExpander Expander;
    public bool DropTarget;
    /// <summary>Reserves the standard icon column without drawing a glyph,
    /// so optional selection checks never move neighboring labels.</summary>
    public bool HideIcon;
}

public static partial class Crystarium
{
    /// <summary>The one animated channel of <c>.row::before</c>.</summary>
    private const int SidebarHighlightChannel = 0;

    /// <summary><c>.expandArrow</c>'s <c>margin-right</c> — the only part
    /// of its box model the row ever sees. Its <c>margin-left:-20px</c>
    /// cancels the 16px width plus this 4px gap exactly, so the arrow box
    /// costs the row no advance at all and lands on the indent gutter that
    /// <c>padding-left</c> opened to its left.</summary>
    private const float SidebarExpanderGap = 4f;

    /// <summary>
    /// 26px sidebar/tree row — transcription of picto
    /// shared/ui/SidebarRow/SidebarRow.module.css.
    ///
    /// <para>The highlight is the <c>::before</c> pseudo-element: inset to
    /// <c>--row-inset</c> on the left, 1px short of the bottom, radius 5,
    /// filled by <c>.row:hover</c> (surface-hover), <c>.selected</c>
    /// (surface-active) or <c>.dropInside</c> (primary-10 over a
    /// primary-30 hairline). Only its OPACITY transitions
    /// (--duration-fast, --ease-default) — the background switches
    /// instantly, so the highlight fades in and vanishes out exactly as
    /// the CSS element does. Content starts at the CSS
    /// <c>padding-left</c>: a row-height icon box with a 2px left margin
    /// carrying a 14px glyph at opacity .85, then the 13px label, then the
    /// right-aligned 12px mono count.</para>
    ///
    /// <para><see cref="SidebarRowProps.Expander"/> costs that content
    /// nothing: <c>.expandArrow</c>'s <c>margin-left:-20px</c> cancels its
    /// own 16px width plus its 4px <c>margin-right</c>, so the arrow is
    /// overlaid on the indent gutter and every row lines up with its
    /// non-expanding siblings at the same indent. Like the CSS, the arrow
    /// is drawn wherever that lands — an indent-0 row with an expander puts
    /// it left of the row, exactly as picto's negative margin would.</para>
    ///
    /// <para>Deviations, all deliberate: <see cref="SidebarRowProps.Selected"/>
    /// beats hover (Picto's <c>.row:hover::before</c> out-specifies
    /// <c>.selected::before</c>, so its hovered selection reads WEAKER —
    /// Poser keeps the selection dominant); and
    /// <see cref="SidebarRowProps.HideIcon"/> keeps the icon box reserved
    /// where CSS would omit the element entirely.</para>
    ///
    /// <para>The whole row is one hit target; picto's separate
    /// <c>.expandArrow</c> <c>onClick</c> (which stops propagation) has no
    /// counterpart here, so a click anywhere — arrow included — returns the
    /// row's click, as it did before the arrow moved.</para>
    /// </summary>
    public static bool SidebarRow(
        string id,
        string label,
        in SidebarRowProps props,
        ControlStyle style = default)
    {
        var theme = ActiveTheme;
        // Content and Fill both resolve to the available region here, so
        // UiWidth.Fixed is the only width path that changes the row.
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / ImGuiHelpers.GlobalScale,
            theme.Controls.ListRowHeight);
        float scale = metrics.Scale;
        float height = metrics.Height;

        // Rows stack seamlessly at exactly 26px (picto sidebar rhythm) — suppress
        // ImGui's ambient vertical ItemSpacing for the reserve.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        uint identity = ImGui.GetID(id);
        var hit = Interactive.Reserve(
            id, new Vector2(metrics.Width, height), disabled: false);
        ImGui.PopStyleVar();
        var dl = ImGui.GetWindowDrawList();

        float inset = (props.Inset > 0f ? props.Inset : 1f) * scale;

        // ── .row::before ─────────────────────────────────────────────
        // Which rule paints the pseudo-element this frame. Transparent
        // means no rule matched, which is also why fading OUT is instant:
        // the CSS background stops applying the moment the state drops.
        var fill = props.DropTarget
            ? theme.Chrome.AccentFill
            : props.Selected
                ? theme.Chrome.SidebarSelected
                : hit.Hovered
                    ? theme.Chrome.SidebarHover
                    : Vector4.Zero;
        Span<MotionChannel> highlight =
        [
            MotionChannel.Number(SidebarHighlightChannel, fill.W > 0f ? 1f : 0f),
        ];
        Motion.Toward(identity, Transition.PictoFast, highlight);
        float pillOpacity = highlight[0].Scalar;
        if (fill.W > 0f && pillOpacity > 0f)
        {
            var border = props.DropTarget
                ? theme.Chrome.AccentFillBorder.Fade(pillOpacity)
                : (Vector4?)null;
            BoxRenderer.Draw(
                dl,
                new Vector2(hit.ScreenMin.X + inset, hit.ScreenMin.Y),
                // bottom: 1px.
                new Vector2(hit.ScreenMax.X, hit.ScreenMax.Y - 1f * scale),
                new BoxStyle
                {
                    BackgroundColor = fill.Fade(pillOpacity),
                    BorderRadius = 5f,
                    BorderWidth = border is null ? 0f : 1f,
                    BorderTopColor = border,
                    BorderRightColor = border,
                    BorderBottomColor = border,
                    BorderLeftColor = border,
                });
        }

        // CSS padding-left: --row-inset is that indent PLUS one pixel.
        float x = hit.ScreenMin.X + inset - 1f * scale;

        // .expandArrow overlays the indent gutter — the 16px box ends one
        // margin-right short of the content start and is pulled entirely
        // back over the padding, so a row that can expand puts its icon,
        // label and badge at exactly the same x as a sibling that cannot.
        // .triangle: a CSS border triangle (3.5px half-width, 5px tall) in
        // text-primary at opacity .7, pointing down when expanded and
        // rotate(-90deg) — apex right — when collapsed. It is right-aligned
        // in the arrow box (justify-content: flex-end) and its own
        // margin-right:-2px pushes it 2px past that edge.
        if (props.Expander != SidebarExpander.None)
        {
            const float half = 3.5f;
            const float rise = 5f * 0.5f;
            const float overhang = 2f;
            var triangleCenter = new Vector2(
                x + (overhang - half - SidebarExpanderGap) * scale,
                hit.ScreenMin.Y + height * 0.5f);
            uint tri = ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.Text.Fade(0.70f)));
            if (props.Expander == SidebarExpander.Open)
            {
                dl.AddTriangleFilled(
                    triangleCenter + new Vector2(-half, -rise) * scale,
                    triangleCenter + new Vector2(half, -rise) * scale,
                    triangleCenter + new Vector2(0f, rise) * scale, tri);
            }
            else
            {
                dl.AddTriangleFilled(
                    triangleCenter + new Vector2(-rise, -half) * scale,
                    triangleCenter + new Vector2(rise, 0f) * scale,
                    triangleCenter + new Vector2(-rise, half) * scale, tri);
            }
        }

        // ── .icon ────────────────────────────────────────────────────
        // A row-height square with a 2px left margin, centering picto's
        // 14px sidebar glyph. Opacity .85, raised to 1 by :hover — which
        // for a game-supplied bitmap is a plain white tint at that alpha,
        // exactly what a CSS opacity does to a non-SVG child.
        float iconOpacity = hit.Hovered ? 1f : 0.85f;
        float glyph = theme.Controls.SmallIconSize * scale;
        float iconMargin = 2f * scale;
        var glyphMin = theme.Optical.Snap(new Vector2(
            x + iconMargin + (height - glyph) * 0.5f,
            hit.ScreenMin.Y + (height - glyph) * 0.5f));
        if (!props.HideIcon && props.IconTexture is { } texture)
        {
            dl.AddImage(
                texture.Handle,
                glyphMin,
                glyphMin + new Vector2(glyph),
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    new Vector4(1f, 1f, 1f, iconOpacity))));
        }
        else if (!props.HideIcon)
        {
            IconIn(
                glyphMin, glyphMin + new Vector2(glyph), props.Icon,
                theme.Text, opacity: iconOpacity);
        }
        x += iconMargin + height;

        // ── .label ───────────────────────────────────────────────────
        // 13px regular text-primary, centered on the row's line box.
        var labelStyle = new TextStyle
        {
            Size = theme.Typography.BodySize,
            Color = theme.Text,
        };
        var labelSize = MeasureText(label, labelStyle);
        TextAt(
            new Vector2(
                x,
                hit.ScreenMin.Y + (height - labelSize.Y) * 0.5f
                    + theme.Optical.SidebarText * scale),
            label,
            labelStyle);

        // ── .count ───────────────────────────────────────────────────
        // 12px mono text-secondary, 4px from the row's right edge.
        if (!string.IsNullOrEmpty(props.Badge))
        {
            var badgeStyle = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Family = FontFamily.Mono,
                Color = theme.TextDim,
            };
            var badgeSize = MeasureText(props.Badge, badgeStyle);
            TextAt(
                new Vector2(
                    hit.ScreenMax.X - theme.Spacing.Two * scale - badgeSize.X,
                    hit.ScreenMin.Y + (height - badgeSize.Y) * 0.5f
                        + theme.Optical.SidebarText * scale),
                props.Badge,
                badgeStyle);
        }

        return hit.Clicked;
    }

    /// <summary>
    /// Sidebar section header — picto SidebarRow.module.css
    /// <c>.sectionTitleRow</c>/<c>.sectionTitle</c>: a 24px row inset by
    /// its 1px margin plus 4px padding, carrying 12px/500 text-tertiary.
    /// </summary>
    public static void SidebarHeader(string text)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float height = 24f * scale;
        var origin = ImGui.GetCursorScreenPos();

        var style = new TextStyle
        {
            Size = theme.Typography.LabelSize,
            Weight = FontWeight.Medium,
            Color = theme.TextMuted,
        };
        var textSize = MeasureText(text, style);
        // margin-left 1 + padding-left 4.
        TextAt(
            origin + new Vector2(5f * scale, (height - textSize.Y) * 0.5f),
            text,
            style);

        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
        ImGui.PopStyleVar();
    }
}
