using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool Dropdown(string id, string[] items, ref int selected)
        => DropdownCore(id, items, ref selected, default, null, false, null, null, null, false);
    public static bool Dropdown(string id, string[] items, ref int selected, in DropdownProps props)
        => DropdownCore(id, items, ref selected, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style,
            props.PreviewText, props.ReselectFires);

    private static bool DropdownCore(string id, string[] items, ref int selected,
        StyleClassSet classes, string? tooltip, bool disabled, Action<int>? onChange, DropdownStyle? inline,
        string? previewText, bool reselectFires)
    {
        Stylesheet.EnsureInitialized();
        if (items.Length == 0) return false;

        var classSet = Cls.Dropdown + classes;
        string popupId = $"{id}_popup";
        bool isOpen = ImGui.IsPopupOpen(popupId);

        var preState = disabled ? PseudoState.Disabled : PseudoState.None;
        if (isOpen) preState |= PseudoState.Open;
        var resolved = Stylesheet.ResolveDropdown(classSet, preState);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        if (resolved.Display == UI.Display.None) return false;

        // Trigger is a single pill — pixel transcription of picto
        // shared/ui/CmSelect/CmSelect.module.css (.btn): 26px, padding 0 6px 0 12px,
        // gap 6, radius 6, bg subtle-overlay white@.10, border 1px white@.08,
        // 12px text; chevron = Tabler IconSelector at 14 in a 20px slot, opacity .5.
        bool changed = false;
        float scale = ImGuiHelpers.GlobalScale;
        float height = (resolved.Height ?? Sizing.Fixed(Theme.Metrics.Control.Workspace)).Value * scale;
        float rounding = (resolved.BorderRadius ?? Theme.Metrics.Radius.Control) * scale;
        float padLeft = Theme.Metrics.Space.Six * scale;
        float padRight = Theme.Metrics.Space.Three * scale;
        float gap = Theme.Metrics.Space.Three * scale;
        float chevronSlot = Theme.Metrics.Control.SwitchHeight * scale;

        float totalWidth;
        if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            totalWidth = resolved.Width.Value.Value * scale;
        else
            totalWidth = Norvrandt.AvailableWidth;
        totalWidth = SizeUtil.Clamp(totalWidth, resolved.MinWidth, resolved.MaxWidth, scale);
        float minWidth = padLeft + gap + chevronSlot + padRight + 20f * scale;
        if (totalWidth < minWidth) totalWidth = minWidth;

        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var valuePos = pos;
        var valueEnd = valuePos + new Vector2(totalWidth, height);
        var btnEnd = valueEnd; // popup positioning below anchors to the pill

        ImGui.SetCursorScreenPos(valuePos);
        ImGui.InvisibleButton($"{id}_value", new Vector2(totalWidth, height));
        bool valueHovered = ImGui.IsItemHovered() && !disabled;
        bool buttonHovered = false;
        if (ImGui.IsItemClicked() && !disabled) ImGui.OpenPopup(popupId);

        var valueBg = ColorEx.ApplyAlpha(resolved.ValueBackground ?? new Vector4(1f, 1f, 1f, 0.10f));
        drawList.AddRectFilled(valuePos, valueEnd, ImGui.ColorConvertFloat4ToU32(valueBg), rounding);

        float borderWidth = (resolved.BorderWidth ?? 1f) * scale;
        if (borderWidth > 0f)
        {
            var borderColor = ColorEx.ApplyAlpha(resolved.BorderColor ?? new Vector4(1f, 1f, 1f, 0.08f));
            float bi = borderWidth * 0.5f; // stroke inside the box like CSS
            drawList.AddRect(valuePos + new Vector2(bi, bi), valueEnd - new Vector2(bi, bi),
                ImGui.ColorConvertFloat4ToU32(borderColor),
                System.MathF.Max(0f, rounding - bi), ImDrawFlags.None, borderWidth);
        }

        // Label at 12px via FontRegistry (CSS-px semantics)
        var fontHandle = FontRegistry.Resolve(resolved.FontFamily ?? FontFamily.Default, resolved.FontSize ?? 12f);
        bool fontPushed = fontHandle is { Available: true };
        if (fontPushed) fontHandle!.Push();

        string currentText = previewText ??
            ((selected >= 0 && selected < items.Length) ? items[selected] : "");
        float textPadding = padLeft;
        float textAvail = totalWidth - padLeft - gap - chevronSlot - padRight;
        string display = TruncateText(currentText, textAvail);
        var textSize = ImGui.CalcTextSize(display);
        var textColor = ColorEx.ApplyAlpha(resolved.Color ?? Norvrandt.Sheet.CurrentTheme.Text);
        // Optical baseline: the font's reported bounds sit one pixel above
        // the visual center of the pill.
        var textPos = new Vector2(
            valuePos.X + padLeft,
            valuePos.Y + (height - textSize.Y) / 2f
                + Theme.Metrics.Optical.DropdownText * scale);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(textColor), display);

        if (fontPushed) fontHandle!.Pop();
        // Truncation-only preview: same chrome, no explanatory delay.
        if (display != currentText && valueHovered)
            HoverHelp.Preview($"{id}-full", valuePos, valueEnd, currentText);

        // Chevron: Tabler IconSelector ("M8 9l4 -4l4 4" + "M16 15l-4 4l-4 -4",
        // 24-grid, stroke 2, round caps) at 14px, opacity .5.
        {
            float iconSpan = Theme.Metrics.Control.SmallIcon * scale;
            float unit = iconSpan / 24f;
            var slotOrigin = new Vector2(valueEnd.X - padRight - chevronSlot, valuePos.Y);
            var origin = slotOrigin + new Vector2((chevronSlot - iconSpan) * 0.5f, (height - iconSpan) * 0.5f);
            var chevColor = ColorEx.ApplyAlpha((resolved.Color ?? Norvrandt.Sheet.CurrentTheme.Text) with { W = 0.5f });
            uint chevU32 = ImGui.ColorConvertFloat4ToU32(chevColor);
            float stroke = 2f * unit;
            drawList.PathLineTo(origin + new Vector2(8f, 9f) * unit);
            drawList.PathLineTo(origin + new Vector2(12f, 5f) * unit);
            drawList.PathLineTo(origin + new Vector2(16f, 9f) * unit);
            drawList.PathStroke(chevU32, ImDrawFlags.None, stroke);
            drawList.PathLineTo(origin + new Vector2(16f, 15f) * unit);
            drawList.PathLineTo(origin + new Vector2(12f, 19f) * unit);
            drawList.PathLineTo(origin + new Vector2(8f, 15f) * unit);
            drawList.PathStroke(chevU32, ImDrawFlags.None, stroke);
        }

        ImGui.SetCursorScreenPos(pos + new Vector2(0, height));

        // Popup
        float popupPadding = Theme.Metrics.Floating.PopupPadding * scale;
        int visibleItems = Math.Min(
            items.Length,
            Theme.Metrics.Picker.MaximumRows);
        float itemListHeight = visibleItems * height;
        float popupHeight = itemListHeight + popupPadding * 2;
        int popupSelected = selected;
        bool popupChanged = false;
        FloatingSurface.Popup(
            popupId,
            new FloatingSurfaceProps
            {
                Width = totalWidth / scale,
                Height = popupHeight / scale,
                Padding = Theme.Metrics.Floating.PopupPadding,
                AnchorMin = valuePos,
                AnchorMax = valueEnd,
            },
            () =>
            {
                float regionWidth = ImGui.GetContentRegionAvail().X / scale;
                ScrollRegion(
                    $"{popupId}-scroll",
                    regionWidth,
                    itemListHeight / scale,
                    region =>
                    {
                        var optFont = FontRegistry.Resolve(
                            resolved.FontFamily ?? FontFamily.Default,
                            resolved.FontSize ?? Theme.Metrics.Typography.Label);
                        bool optFontPushed = optFont is { Available: true };
                        if (optFontPushed) optFont!.Push();
                        float optPad = Theme.Metrics.Page.ActionGap * scale;
                        float optRounding = Theme.Metrics.Radius.Medium * scale;
                        uint optFill = ImGui.ColorConvertFloat4ToU32(
                            ColorEx.ApplyAlpha(
                                new Vector4(1f, 1f, 1f, 0.08f)));
                        var spacing = ImGui.GetStyle().ItemSpacing;
                        ImGui.PushStyleVar(
                            ImGuiStyleVar.ItemSpacing,
                            new Vector2(spacing.X, 0f));

                        for (int i = 0; i < items.Length; i++)
                        {
                            bool isSelected = i == popupSelected;
                            var itemPos = ImGui.GetCursorScreenPos();
                            var itemSize = new Vector2(
                                region.ContentWidth * scale,
                                height);
                            if (i > 0)
                            {
                                ImGui.GetWindowDrawList().AddRectFilled(
                                    itemPos,
                                    new Vector2(
                                        itemPos.X + itemSize.X,
                                        itemPos.Y + MathF.Max(1f, scale)),
                                    ImGui.ColorConvertFloat4ToU32(
                                        Norvrandt.Sheet.CurrentTheme.Border
                                            with { W = 0.24f }));
                            }

                            ImGui.PushID(i);
                            if (ImGui.InvisibleButton("##item", itemSize))
                            {
                                if (popupSelected != i || reselectFires)
                                {
                                    popupSelected = i;
                                    popupChanged = true;
                                    onChange?.Invoke(i);
                                }
                                ImGui.CloseCurrentPopup();
                            }
                            bool itemHovered = ImGui.IsItemHovered();

                            var popupDrawList = ImGui.GetWindowDrawList();
                            if (itemHovered || isSelected)
                                popupDrawList.AddRectFilled(
                                    itemPos,
                                    itemPos + itemSize,
                                    optFill,
                                    optRounding);

                            string itemDisplay = TruncateText(
                                items[i],
                                itemSize.X - optPad * 2f);
                            var itemTextSize = ImGui.CalcTextSize(itemDisplay);
                            var itemTextPos = new Vector2(
                                itemPos.X + optPad,
                                itemPos.Y + (height - itemTextSize.Y) * 0.5f
                                    + Theme.Metrics.Optical.DropdownText * scale);
                            popupDrawList.AddText(
                                itemTextPos,
                                ColorEx.ApplyAlpha(
                                    Norvrandt.Sheet.CurrentTheme.Text).ToU32(),
                                itemDisplay);
                            if (itemDisplay != items[i] && itemHovered)
                                HoverHelp.Preview(
                                    $"{id}-item-{i}",
                                    itemPos,
                                    itemPos + itemSize,
                                    items[i]);

                            ImGui.PopID();
                        }

                        ImGui.PopStyleVar();
                        if (optFontPushed) optFont!.Pop();
                    });
            });

        if (popupChanged)
        {
            selected = popupSelected;
            changed = true;
        }

        if (!string.IsNullOrEmpty(tooltip) && (valueHovered || buttonHovered))
            HoverHelp.Explain(id, valuePos, valueEnd, tooltip!);

        return changed;
    }

    private static string TruncateText(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var size = ImGui.CalcTextSize(text);
        if (size.X <= maxWidth) return text;
        var ellipsisSize = ImGui.CalcTextSize("...");
        float available = maxWidth - ellipsisSize.X;
        if (available <= 0) return "...";
        int left = 0, right = text.Length;
        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            var sub = ImGui.CalcTextSize(text[..mid]);
            if (sub.X <= available) left = mid; else right = mid - 1;
        }
        return left == 0 ? "..." : text[..left] + "...";
    }
}
