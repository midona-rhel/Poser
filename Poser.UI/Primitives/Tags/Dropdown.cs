using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// <c>.drop max-height: calc(7 * 26px + 6 * 2px + 12px)</c> — the
    /// dropdown scrolls past seven rows. A CSS literal, not a token: the
    /// shared Picker.MaximumRows belongs to the search pickers.
    /// </summary>
    private const int DropVisibleRows = 7;

    /// <summary>CmSelect.tsx places the portal at
    /// <c>top: cssRect.bottom + 4</c>.</summary>
    private const float DropAnchorGap = 4f;

    /// <summary><c>.btnChevron { width: 20px }</c> — the fixed slot the
    /// 14px IconSelector centers in.</summary>
    private const float ChevronSlot = 20f;

    /// <summary><c>.btnChevron { opacity: .5 }</c>.</summary>
    private const float ChevronOpacity = 0.5f;

    /// <summary>Tabler <c>IconSelector</c>, the glyph CmSelect.tsx
    /// renders in <c>.btnChevron</c>.</summary>
    private const string ChevronIcon = "selector";

    /// <summary>
    /// The OPAQUE fill <c>.drop</c> wears — the color the trigger already
    /// SHOWS at rest, not the token it is painted with.
    ///
    /// <para><c>.btn</c> and <c>.drop</c> share
    /// <c>--color-subtle-overlay</c>, which is white at 10%: a TRANSLUCENT
    /// token whose result belongs to whatever sits behind it. Behind the
    /// trigger is the window surface; behind the popup is the popup's own
    /// <c>--shadow-panel</c>, whose solid core <see cref="BoxRenderer"/>
    /// lays down before the fill. The one token therefore reads visibly
    /// darker on the menu than on the closed control the menu is supposed
    /// to continue. Flattening it over <see cref="Theme.Surface"/> yields
    /// that resting appearance as an opaque color, and the opacity is the
    /// point: shadow, blur, or whatever the menu overhangs can no longer
    /// tint it.</para>
    ///
    /// <para>The BORDER token is deliberately NOT flattened.
    /// <see cref="BoxRenderer"/> strokes the border over the fill, so
    /// <c>--color-border-secondary</c> over this opaque fill composites to
    /// the trigger's border pixel by construction — flattening it first
    /// would compute the identical number a second time and hide where the
    /// match comes from.</para>
    /// </summary>
    private static Vector4 DropdownPopupFill(in Theme theme) =>
        ColorEx.FlattenOver(theme.Chrome.ControlHover, theme.Surface);

    public static bool Dropdown(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, null, false);

    public static bool ActionDropdown(
        string id, string[] items, int selected, string previewText, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, previewText, true);

    /// <summary>
    /// Picto's <c>shared/ui/CmSelect</c>. The trigger is <c>.btn</c>
    /// (26px, <c>padding: 0 6px 0 12px</c>, <c>gap: 6px</c>, radius 6,
    /// <c>--color-subtle-overlay</c> over a 1px
    /// <c>--color-border-secondary</c>, 12px <c>--color-text-primary</c>,
    /// then the 20px <c>.btnChevron</c> slot); the portal is
    /// <c>.drop</c> (the same 1px border and radius, 4px padding, 2px
    /// row gap, <c>--shadow-panel</c>) over
    /// <c>.opt</c> rows (26px, <c>padding: 0 8px</c>, radius 4,
    /// <c>--color-hover-overlay</c> for both <c>:hover</c> and
    /// <c>.optActive</c>). The module declares no <c>:hover</c> and no
    /// <c>transition</c> on <c>.btn</c>, so the trigger has no hover
    /// paint and no motion channel.
    /// One product deviation: <c>.drop</c> takes the trigger's APPEARANCE
    /// instead of <c>--glass-bg</c>, so the open dropdown reads as one
    /// continuous control. Appearance, not token: see
    /// <see cref="DropdownPopupFill"/>.
    /// </summary>
    private static bool DropdownCore(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style, bool disabled, string? help,
        string? previewText, bool reselectFires)
    {
        if (items.Length == 0) return false;
        string popupId = Ids.Join(id, "_popup");
        bool changed = false;
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;

        var metrics = MeasureDropdown(items, previewText, style);
        float borderPx = metrics.BorderPx;
        float padLeft = metrics.PadLeft;
        float padRight = metrics.PadRight;
        float gap = metrics.Gap;
        float chevronSlot = metrics.ChevronSlot;
        var labelStyle = metrics.LabelStyle;
        float height = metrics.Height;
        float totalWidth = metrics.Width;

        var pos = ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(pos);
        var trigger = Interactive.Reserve(
            Ids.Join(id, "_value"), new Vector2(totalWidth, height), disabled);
        bool valueHovered = trigger.Hovered;
        if (trigger.Clicked)
            OpenPopover(popupId);
        var valueMin = trigger.ScreenMin;
        var valueMax = trigger.ScreenMax;

        var boxPaint = PaintDropdownBox(trigger, disabled);
        var labelColor = boxPaint.LabelColor;
        float chevronOpacity = boxPaint.ChevronOpacity;

        // CSS content box: the 1px border is inside the border box, so
        // padding measures from the border's INNER edge.
        float contentLeft = valueMin.X + borderPx + padLeft;
        float contentRight = valueMax.X - borderPx - padRight;
        float chevronLeft = contentRight - chevronSlot;
        float labelWidth = chevronLeft - gap - contentLeft;

        string currentText = previewText ??
            ((selected >= 0 && selected < items.Length) ? items[selected] : "");
        var triggerLabelStyle = labelStyle with { Color = labelColor };
        bool labelClipped = false;
        if (labelWidth > 0f && currentText.Length > 0)
        {
            var measured = MeasureText(currentText, triggerLabelStyle);
            labelClipped = measured.X > labelWidth;
            // `align-items: center` on the 24px content box, on the INK:
            // TextInBand's metric seat replaces the line-box centre, which
            // reads low. No per-surface nudge on top of it.
            TextInBand(
                new Vector2(contentLeft, valueMin.Y),
                new Vector2(labelWidth, height),
                currentText,
                triggerLabelStyle,
                TextConstraint.Truncate(labelWidth));
        }

        // Truncation-only preview: same chrome, no explanatory delay.
        if (labelClipped && valueHovered)
            HoverHelp.Preview(
                Ids.Join(id, "-full"), valueMin, valueMax, currentText);

        // .btnChevron: <IconSelector size={14} /> centered in the 20px slot.
        float iconSpan = theme.Controls.SmallIconSize * scale;
        var iconMin = new Vector2(
            chevronLeft + (chevronSlot - iconSpan) * 0.5f,
            valueMin.Y + (height - iconSpan) * 0.5f);
        IconIn(
            iconMin,
            iconMin + new Vector2(iconSpan),
            ChevronIcon,
            opacity: chevronOpacity);

        ImGui.SetCursorScreenPos(pos + new Vector2(0, height));

        // ---- .drop ----------------------------------------------------
        var popupMetrics =
            MeasureDropdownPopup(items.Length, metrics.LogicalHeight);
        int visibleItems = popupMetrics.VisibleItems;
        float rowHeight = popupMetrics.RowHeight;
        float rowGap = popupMetrics.RowGap;
        float itemListHeight = popupMetrics.ItemListHeight;
        float dropInset = popupMetrics.DropInset;
        float popupHeight = popupMetrics.PopupHeight;
        int popupSelected = selected;
        bool popupChanged = false;
        FloatingSurface.Popup(
            popupId,
            new FloatingSurfaceProps
            {
                Width = totalWidth / scale,
                Height = popupHeight / scale,
                Padding = dropInset / scale,
                AnchorMin = valueMin,
                AnchorMax = valueMax + new Vector2(
                    0f, popupMetrics.AnchorGapCompensation),
                Treatment = FloatingSurfaceTreatment.Unframed,
            },
            () =>
            {
                var popupMin = ImGui.GetWindowPos();
                var popupMax = popupMin + ImGui.GetWindowSize();
                var popupDrawList = ImGui.GetWindowDrawList();
                PaintDropdownSurface(popupDrawList, popupMin, popupMax);

                float regionWidth = ImGui.GetContentRegionAvail().X / scale;
                ScrollRegion(
                    Ids.Join(popupId, "-scroll"),
                    regionWidth,
                    itemListHeight / scale,
                    region =>
                    {
                        float optPad = theme.Spacing.Four * scale; // padding: 0 8px
                        float optRadius = theme.Radii.Medium * scale;
                        var spacing = ImGui.GetStyle().ItemSpacing;
                        ImGui.PushStyleVar(
                            ImGuiStyleVar.ItemSpacing,
                            new Vector2(spacing.X, 0f));

                        for (int i = 0; i < items.Length; i++)
                        {
                            bool isSelected = i == popupSelected;
                            var itemPos = ImGui.GetCursorScreenPos();
                            bool scrolls = items.Length > visibleItems;
                            var hitSize = new Vector2(
                                (scrolls ? region.ContentWidth : regionWidth) * scale,
                                rowHeight);
                            var fillSize = new Vector2(regionWidth * scale, rowHeight);

                            ImGui.PushID(i);
                            var itemHit = Interactive.Reserve(
                                "##item", hitSize, disabled: false);
                            if (itemHit.Clicked)
                            {
                                if (popupSelected != i || reselectFires)
                                {
                                    popupSelected = i;
                                    popupChanged = true;
                                    onChange(i);
                                }
                                ImGui.CloseCurrentPopup();
                            }
                            bool itemHovered = itemHit.Hovered;

                            if (isSelected || itemHovered)
                                PaintDropdownRowFill(
                                    popupDrawList, itemPos, fillSize, optRadius);

                            float optWidth = hitSize.X - optPad * 2f;
                            bool optClipped = false;
                            if (optWidth > 0f && items[i].Length > 0)
                            {
                                var optSize = MeasureText(items[i], labelStyle);
                                optClipped = optSize.X > optWidth;
                                TextInBand(
                                    new Vector2(itemPos.X + optPad, itemPos.Y),
                                    new Vector2(optWidth, rowHeight),
                                    items[i],
                                    labelStyle,
                                    TextConstraint.Truncate(optWidth));
                            }
                            if (optClipped && itemHovered)
                                HoverHelp.Preview(
                                    Ids.Join(id, "-item-", i),
                                    itemPos,
                                    itemPos + hitSize,
                                    items[i]);

                            ImGui.PopID();
                            if (i < items.Length - 1)
                                ImGui.SetCursorScreenPos(new Vector2(
                                    itemPos.X,
                                    itemPos.Y + rowHeight + rowGap));
                        }

                        ImGui.PopStyleVar();
                    });
            });

        if (popupChanged)
        {
            changed = true;
        }

        if (!string.IsNullOrEmpty(help) && valueHovered)
            HoverHelp.Explain(id, valueMin, valueMax, help!);

        return changed;
    }

    /// <summary>
    /// The trigger's resolved box plus the geometry every piece of it is
    /// measured from. Pixel fields are already scaled;
    /// <see cref="LogicalHeight"/> is the unscaled span
    /// <see cref="ControlSizing"/> resolved.
    /// </summary>
    private readonly struct DropdownMetrics
    {
        public DropdownMetrics(
            float width, float height, float logicalHeight, float widestLabel,
            float borderPx, float padLeft, float padRight, float gap,
            float chevronSlot, TextStyle labelStyle)
        {
            Width = width;
            Height = height;
            LogicalHeight = logicalHeight;
            WidestLabel = widestLabel;
            BorderPx = borderPx;
            PadLeft = padLeft;
            PadRight = padRight;
            Gap = gap;
            ChevronSlot = chevronSlot;
            LabelStyle = labelStyle;
        }

        /// <summary>Resolved width in pixels, already floored to the
        /// chevron-plus-padding minimum.</summary>
        public readonly float Width;
        public readonly float Height;
        public readonly float LogicalHeight;
        /// <summary>The widest option (or preview) in pixels — the span the
        /// invisible <c>.sizer</c> rows force the label area to.</summary>
        public readonly float WidestLabel;
        public readonly float BorderPx;
        public readonly float PadLeft;
        public readonly float PadRight;
        public readonly float Gap;
        public readonly float ChevronSlot;
        /// <summary>The 12px <c>.btn</c> font both the trigger label and the
        /// <c>.opt</c> rows are measured and drawn with.</summary>
        public readonly TextStyle LabelStyle;
    }

    /// <summary>
    /// CmSelect's base contract is intrinsic sizing: the invisible
    /// <c>.sizer</c> spans force the label area to the WIDEST option, so
    /// Content/Unspecified must never inherit the surrounding region. Fixed
    /// and Fill may still override the resolved width. The intrinsic span is
    /// only known after measuring the options, so the shared sizing preamble
    /// runs here rather than at the top of the control.
    /// </summary>
    private static DropdownMetrics MeasureDropdown(
        string[] items, string? previewText, ControlStyle style)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float borderPx = 1f * scale;                 // border: 1px solid
        float padLeft = theme.Spacing.Six * scale;   // padding-left: 12px
        float padRight = theme.Spacing.Three * scale;// padding-right: 6px
        float gap = theme.Spacing.Three * scale;     // gap: 6px
        float chevronSlot = ChevronSlot * scale;

        // .btn font: 12px --font-family at --color-text-primary.
        var labelStyle = new TextStyle { Size = theme.Typography.LabelSize };

        float widestLabel = 0f;
        foreach (string item in items)
            widestLabel = MathF.Max(widestLabel, MeasureText(item, labelStyle).X);
        if (!string.IsNullOrEmpty(previewText))
            widestLabel = MathF.Max(
                widestLabel, MeasureText(previewText!, labelStyle).X);
        // CSS border-box: both borders + both paddings + label + gap + slot.
        float intrinsicWidth =
            borderPx * 2f + padLeft + widestLabel + gap + chevronSlot + padRight;

        var resolved = ControlSizing.Resolve(
            style, intrinsicWidth / scale, theme.Controls.WorkspaceHeight);
        float totalWidth = resolved.Width;
        float minWidth =
            borderPx * 2f + padLeft + gap + chevronSlot + padRight + 20f * scale;
        if (totalWidth < minWidth) totalWidth = minWidth;
        // The floor keeps a free-standing trigger usable; a track cap is a
        // containment contract, so it outranks the floor and the label
        // absorbs the squeeze.
        totalWidth = ControlSizing.Cap(totalWidth / scale, style.MaxWidth) * scale;

        return new DropdownMetrics(
            totalWidth, resolved.Height, resolved.LogicalHeight, widestLabel,
            borderPx, padLeft, padRight, gap, chevronSlot, labelStyle);
    }

    /// <summary>
    /// What the closed box hands its two pieces of content: the label's
    /// color and the chevron's effective <c>.btnChevron</c> opacity.
    /// </summary>
    private readonly struct DropdownTriggerPaint
    {
        public DropdownTriggerPaint(Vector4 labelColor, float chevronOpacity)
        {
            LabelColor = labelColor;
            ChevronOpacity = chevronOpacity;
        }

        public readonly Vector4 LabelColor;
        public readonly float ChevronOpacity;
    }

    /// <summary>
    /// The closed trigger's BOX alone — fill, border, and the disabled
    /// group, returning what the label and chevron must take from it.
    /// </summary>
    private static DropdownTriggerPaint PaintDropdownBox(
        in InteractionResult hit, bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        var drawList = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control;           // border-radius: 6px
        float borderPx = 1f * scale;                  // border: 1px solid
        var triggerFill = theme.Chrome.ControlHover;  // --color-subtle-overlay
        var triggerBorder = theme.Border;             // --color-border-secondary
        var labelColor = theme.Text;                  // --color-text-primary

        if (disabled)
        {
            // CmSelect declares NO :disabled rule; this borrows the Picto
            // action-button family's `.btn:disabled { opacity: .35 }`
            // GROUP opacity — the SAME recipe Button uses, so it comes
            // from the one implementation rather than a second copy. The
            // trigger has two pieces of content inside that group and
            // both take their transform from the same return value: the
            // label compensates, the chevron scales its `.btnChevron`
            // opacity.
            var content = ControlPaint.DisabledGroup(
                drawList, hit.ScreenMin, hit.ScreenMax,
                radius * scale, borderPx, triggerFill, triggerBorder,
                theme.Chrome.ControlDisabledOpacity);
            return new DropdownTriggerPaint(
                content.Label(labelColor), content.Glyph(ChevronOpacity));
        }

        BoxRenderer.Draw(drawList, hit.ScreenMin, hit.ScreenMax, new BoxStyle
        {
            BackgroundColor = triggerFill,
            BorderWidth = 1f,
            BorderRadius = radius,
            BorderTopColor = triggerBorder,
            BorderRightColor = triggerBorder,
            BorderBottomColor = triggerBorder,
            BorderLeftColor = triggerBorder,
        });
        return new DropdownTriggerPaint(labelColor, ChevronOpacity);
    }

    /// <summary>
    /// The open <c>.drop</c> panel itself. The <c>--shadow-panel</c> pair
    /// must escape the popup window's own clip, so the surface is drawn
    /// against the full display rect.
    /// </summary>
    private static void PaintDropdownSurface(
        ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        var theme = ActiveTheme;
        var border = theme.Border;
        drawList.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
        BoxRenderer.Draw(drawList, min, max, new BoxStyle
        {
            // PRODUCT DEVIATION from `.drop { background: var(--glass-bg) }`:
            // the popup wears the TRIGGER's own surface so the open
            // control reads as one object. Opaque by construction —
            // see DropdownPopupFill — and the same border token as `.btn`;
            // the CSS glass recipe is not used.
            BackgroundColor = DropdownPopupFill(theme),
            BorderWidth = 1f,
            BorderRadius = theme.Radii.Control,
            BorderTopColor = border,
            BorderRightColor = border,
            BorderBottomColor = border,
            BorderLeftColor = border,
            BoxShadows = [theme.Shadows.Panel, theme.Shadows.PanelRing],
        });
        drawList.PopClipRect();
    }

    /// <summary>
    /// One <c>.opt</c> row's state fill. <c>.opt:hover</c> is
    /// <c>--color-menu-hover</c> and <c>.optActive</c> is
    /// <c>--color-hover-overlay</c> — the SAME token, and <c>:hover</c>
    /// outranks <c>.optActive</c> on specificity, so one fill covers both
    /// states. The caller decides WHEN a row is filled.
    /// </summary>
    private static void PaintDropdownRowFill(
        ImDrawListPtr drawList, Vector2 pos, Vector2 fillSize, float radius)
    {
        uint rowFill = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(ActiveTheme.Chrome.WeakOverlay));
        drawList.AddRectFilled(pos, pos + fillSize, rowFill, radius);
    }

    /// <summary>
    /// The open panel's box: the row list it scrolls, the content inset that
    /// surrounds it, and the anchor nudge CmSelect's 4px gap needs on top of
    /// the shared anchored placement. All spans are pixels.
    /// </summary>
    private readonly struct DropdownPopupMetrics
    {
        public DropdownPopupMetrics(
            int visibleItems, float rowHeight, float rowGap, float dropInset,
            float itemListHeight, float popupHeight, float anchorGapCompensation)
        {
            VisibleItems = visibleItems;
            RowHeight = rowHeight;
            RowGap = rowGap;
            DropInset = dropInset;
            ItemListHeight = itemListHeight;
            PopupHeight = popupHeight;
            AnchorGapCompensation = anchorGapCompensation;
        }

        public readonly int VisibleItems;
        public readonly float RowHeight;
        public readonly float RowGap;
        /// <summary><c>.drop</c> is content-box: its own 1px border plus the
        /// 4px padding sit outside the row list, so the popup's content
        /// inset is both.</summary>
        public readonly float DropInset;
        public readonly float ItemListHeight;
        public readonly float PopupHeight;
        /// <summary>The shared anchored placement already adds
        /// <c>Floating.AnchorGap</c>; CmSelect asks for
        /// <see cref="DropAnchorGap"/>, so the anchor carries the rest.
        /// </summary>
        public readonly float AnchorGapCompensation;
    }

    /// <summary>
    /// Sizes the <c>.drop</c> panel from the row count and the trigger's own
    /// resolved height — <c>.opt</c> rows are the trigger's 26px tall, and
    /// the list scrolls past <see cref="DropVisibleRows"/>.
    /// </summary>
    private static DropdownPopupMetrics MeasureDropdownPopup(
        int itemCount, float triggerLogicalHeight)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        int visibleItems = Math.Min(itemCount, DropVisibleRows);
        float rowHeight = triggerLogicalHeight * scale;        // .opt height: 26px
        float rowGap = theme.Floating.DropdownRowGap * scale;  // gap: 2px
        float itemListHeight =
            visibleItems * rowHeight + Math.Max(0, visibleItems - 1) * rowGap;
        float dropInset = 1f * scale + theme.Floating.PopupPadding * scale;
        float popupHeight = itemListHeight + dropInset * 2f;
        return new DropdownPopupMetrics(
            visibleItems, rowHeight, rowGap, dropInset, itemListHeight,
            popupHeight,
            (DropAnchorGap - theme.Floating.AnchorGap) * scale);
    }
}
