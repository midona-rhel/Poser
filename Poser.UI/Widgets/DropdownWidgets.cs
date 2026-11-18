using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.PopoverWidgets;
using static Poser.UI.Widgets.ScrollRegionWidgets;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class DropdownWidgets
{
    /// <summary>The dropdown scrolls after seven visible rows.</summary>
    private const int DropVisibleRows = 7;

    /// <summary>Gap between the closed box and the open list.</summary>
    private const float DropAnchorGap = 4f;

    /// <summary>Fixed width of the space at the right that the arrow icon is centred in.</summary>
    private const float ChevronSlot = 20f;

    /// <summary>Opacity of the arrow icon.</summary>
    private const float ChevronOpacity = 0.5f;

    /// <summary>Name of the arrow icon.</summary>
    private const string ChevronIcon = "selector";

    public static bool Dropdown(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null,
        bool disruptive = false) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, null, false, disruptive);

    public static bool ActionDropdown(
        string id, string[] items, int selected, string previewText, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null,
        TablerIcon? icon = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, previewText, true,
            triggerIcon: icon is { } glyph ? Tabler.NameFor(glyph) : ChevronIcon);

    /// <summary>Draws a dropdown: the closed box and the list that opens from it.</summary>
    private static bool DropdownCore(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style, bool disabled, string? help,
        string? previewText, bool reselectFires, bool disruptive = false,
        string triggerIcon = ChevronIcon)
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
        disabled |= Interactive.GreyedOut;
        var trigger = Interactive.Reserve(
            Ids.Join(id, "_value"), new Vector2(totalWidth, height), disabled);
        bool valueHovered = trigger.Hovered;
        if (trigger.Clicked)
            OpenPopover(popupId);
        var valueMin = trigger.ScreenMin;
        var valueMax = trigger.ScreenMax;

        var boxPaint = PaintDropdownBox(trigger, disabled);
        // A dropdown marked disruptive gets the purple outline, as a button does.
        if (disruptive)
            ImGui.GetWindowDrawList().AddRect(
                valueMin + new Vector2(0.5f),
                valueMax - new Vector2(0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    theme.Chrome.Disruptive.Fade(disabled ? theme.Chrome.DisabledOpacity : 1f))),
                theme.Radii.Control * scale, ImDrawFlags.RoundCornersAll, 1.5f * scale);
        var labelColor = boxPaint.LabelColor;
        float chevronOpacity = boxPaint.ChevronOpacity;

        // The padding starts inside the box's border.
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
            // TextInBand centres the visible letters in the box, not the font's line height.
            TextInBand(
                new Vector2(contentLeft, valueMin.Y),
                new Vector2(labelWidth, height),
                currentText,
                triggerLabelStyle,
                TextConstraint.Truncate(labelWidth));
        }

        // When the label is cut off, hovering shows the full text at once, styled like a tooltip.
        if (labelClipped && valueHovered)
            HoverHelp.Preview(
                Ids.Join(id, "-full"), valueMin, valueMax, currentText);

        // Centre the arrow icon in its fixed space at the right.
        float iconSpan = theme.Controls.SmallIconSize * scale;
        var iconMin = new Vector2(
            chevronLeft + (chevronSlot - iconSpan) * 0.5f,
            valueMin.Y + (height - iconSpan) * 0.5f);
        IconIn(
            iconMin,
            iconMin + new Vector2(iconSpan),
            triggerIcon,
            opacity: chevronOpacity);

        ImGui.SetCursorScreenPos(pos + new Vector2(0, height));

        // ---- the open list -------------------------------------------
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
                        float optPad = theme.Spacing.Four * scale;
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
                            // The highlight stops where the rows stop: a margin
                            // short of the scrollbar when there is one.
                            var fillSize = hitSize;

                            ImGui.PushID(i);
                            var itemHit = Interactive.Reserve(
                                "##item", hitSize, disabled: false);
                            if (itemHit.Clicked)
                            {
                                if (popupSelected != i || reselectFires)
                                {
                                    popupSelected = i;
                                    popupChanged = true;
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
            // Run the choice only after the list has closed. A dialog the choice
            // opens would otherwise count as part of the list and close with it.
            onChange(popupSelected);
        }

        if (!string.IsNullOrEmpty(help) && valueHovered)
            HoverHelp.Explain(id, valueMin, valueMax, help!);

        return changed;
    }

    /// <summary>
    /// The size of the closed box and the measurements its parts are drawn
    /// from. Pixel values already include the interface scale;
    /// <see cref="LogicalHeight"/> is the height before scaling, as
    /// <see cref="ControlSizing"/> worked it out.
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

        /// <summary>Resolved width in pixels after the minimum and MaxWidth cap are applied.</summary>
        public readonly float Width;
        public readonly float Height;
        public readonly float LogicalHeight;
        /// <summary>The widest option or preview in pixels.</summary>
        public readonly float WidestLabel;
        public readonly float BorderPx;
        public readonly float PadLeft;
        public readonly float PadRight;
        public readonly float Gap;
        public readonly float ChevronSlot;
        /// <summary>The text style shared by the closed box and the list's rows.</summary>
        public readonly TextStyle LabelStyle;
    }

    /// <summary>Measures the width the dropdown needs for its widest option.
    /// A fixed width or a fill-the-row width can override it.</summary>
    private static DropdownMetrics MeasureDropdown(
        string[] items, string? previewText, ControlStyle style)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float borderPx = 1f * scale;
        float padLeft = theme.Spacing.Six * scale;
        float padRight = theme.Spacing.Three * scale;
        float gap = theme.Spacing.Three * scale;
        float chevronSlot = ChevronSlot * scale;

        var labelStyle = new TextStyle { Size = theme.Typography.LabelSize };

        float widestLabel = 0f;
        foreach (string item in items)
            widestLabel = MathF.Max(widestLabel, MeasureText(item, labelStyle).X);
        if (!string.IsNullOrEmpty(previewText))
            widestLabel = MathF.Max(
                widestLabel, MeasureText(previewText!, labelStyle).X);
        // The full width: both borders, the padding, the label, the gap and the arrow icon.
        float intrinsicWidth =
            borderPx * 2f + padLeft + widestLabel + gap + chevronSlot + padRight;

        var resolved = ControlSizing.Resolve(
            style, intrinsicWidth / scale, theme.Controls.WorkspaceHeight);
        float totalWidth = resolved.Width;
        float minWidth =
            borderPx * 2f + padLeft + gap + chevronSlot + padRight + 20f * scale;
        if (totalWidth < minWidth) totalWidth = minWidth;
        // The minimum keeps a dropdown on its own usable. A maximum width set
        // by the surrounding layout must always hold, so it wins over the
        // minimum and the label is cut shorter instead.
        totalWidth = ControlSizing.Cap(totalWidth / scale, style.MaxWidth) * scale;

        return new DropdownMetrics(
            totalWidth, resolved.Height, resolved.LogicalHeight, widestLabel,
            borderPx, padLeft, padRight, gap, chevronSlot, labelStyle);
    }

    /// <summary>How the label and arrow icon of the closed box are drawn.</summary>
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

    /// <summary>Draws the closed box and returns how its label and icon are drawn.</summary>
    private static DropdownTriggerPaint PaintDropdownBox(
        in InteractionResult hit, bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        var drawList = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control;
        float borderPx = 1f * scale;
        var triggerFill = theme.Chrome.ControlHover;
        var triggerBorder = theme.Border;
        var labelColor = theme.Text;

        if (disabled)
        {
            // When disabled, the label and the icon are dimmed together, the same way.
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

    /// <summary>The open list's colour: the closed box's hover colour mixed
    /// onto the page colour. The list is fully opaque and looks like the box
    /// grew taller.</summary>
    private static Vector4 DropdownPopupFill(in Theme theme) =>
        ColorEx.FlattenOver(theme.Chrome.ControlHover, theme.Surface);

    /// <summary>Draws the popup background, border, and shadows using a display-wide
    /// clip rectangle so the shadows can extend outside the popup.</summary>
    private static void PaintDropdownSurface(
        ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        var theme = ActiveTheme;
        var border = theme.Border;
        drawList.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
        BoxRenderer.Draw(drawList, min, max, new BoxStyle
        {
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

    /// <summary>Paints one active or hovered option row.</summary>
    private static void PaintDropdownRowFill(
        ImDrawListPtr drawList, Vector2 pos, Vector2 fillSize, float radius)
    {
        uint rowFill = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(ActiveTheme.Chrome.WeakOverlay));
        drawList.AddRectFilled(pos, pos + fillSize, rowFill, radius);
    }

    /// <summary>Pixel measurements for the open list.</summary>
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
        /// <summary>Border and padding outside the row list.</summary>
        public readonly float DropInset;
        public readonly float ItemListHeight;
        public readonly float PopupHeight;
        /// <summary>Extra space between the box and the list, beyond what the shared popup placement leaves.</summary>
        public readonly float AnchorGapCompensation;
    }

    /// <summary>Sizes the open list from the closed box's height and the number of visible rows.</summary>
    private static DropdownPopupMetrics MeasureDropdownPopup(
        int itemCount, float triggerLogicalHeight)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        int visibleItems = Math.Min(itemCount, DropVisibleRows);
        float rowHeight = triggerLogicalHeight * scale;
        float rowGap = theme.Floating.DropdownRowGap * scale;
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
