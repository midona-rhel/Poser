using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>The dropdown scrolls after seven visible rows.</summary>
    private const int DropVisibleRows = 7;

    /// <summary>Gap between the trigger and popup.</summary>
    private const float DropAnchorGap = 4f;

    /// <summary>Fixed slot that centers the selector glyph.</summary>
    private const float ChevronSlot = 20f;

    /// <summary>Selector glyph opacity.</summary>
    private const float ChevronOpacity = 0.5f;

    /// <summary>Selector glyph name.</summary>
    private const string ChevronIcon = "selector";

    public static bool Dropdown(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, null, false);

    public static bool ActionDropdown(
        string id, string[] items, int selected, string previewText, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, previewText, true);

    /// <summary>Draws the shared dropdown trigger and glass popup.</summary>
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

        // Padding begins inside the trigger border.
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
            // TextInBand centers the measured glyph ink in the control.
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

        // Center the selector glyph in its fixed trailing slot.
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

        // ---- popup ---------------------------------------------------
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
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            () =>
            {
                var popupDrawList = ImGui.GetWindowDrawList();

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
        /// <summary>The widest option or preview in pixels.</summary>
        public readonly float WidestLabel;
        public readonly float BorderPx;
        public readonly float PadLeft;
        public readonly float PadRight;
        public readonly float Gap;
        public readonly float ChevronSlot;
        /// <summary>The text style shared by the trigger and option rows.</summary>
        public readonly TextStyle LabelStyle;
    }

    /// <summary>Measures intrinsic width from the widest option. Fixed and
    /// fill sizing may override that width.</summary>
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
        // The border box includes both borders, padding, label, gap, and glyph.
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

    /// <summary>Text and glyph treatment for the closed trigger.</summary>
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

    /// <summary>Paints the closed trigger and returns its content treatment.</summary>
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
            // The shared disabled group keeps the label and glyph consistent.
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

    /// <summary>Paints one active or hovered option row.</summary>
    private static void PaintDropdownRowFill(
        ImDrawListPtr drawList, Vector2 pos, Vector2 fillSize, float radius)
    {
        uint rowFill = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(ActiveTheme.Chrome.WeakOverlay));
        drawList.AddRectFilled(pos, pos + fillSize, rowFill, radius);
    }

    /// <summary>Pixel measurements for the open dropdown surface.</summary>
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
        /// <summary>Additional gap beyond the shared anchored placement.</summary>
        public readonly float AnchorGapCompensation;
    }

    /// <summary>Sizes the popup from the trigger and visible row count.</summary>
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
