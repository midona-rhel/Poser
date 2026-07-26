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
        float height = (resolved.Height ?? Sizing.Fixed(26f)).Value * scale;
        float rounding = (resolved.BorderRadius ?? 6f) * scale;
        float padLeft = 12f * scale;
        float padRight = 6f * scale;
        float gap = 6f * scale;
        float chevronSlot = 20f * scale;

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
        var textPos = new Vector2(valuePos.X + padLeft, valuePos.Y + (height - textSize.Y) / 2f + scale);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(textColor), display);

        if (fontPushed) fontHandle!.Pop();
        // Truncation-only preview: same chrome, no explanatory delay.
        if (display != currentText && valueHovered)
            HoverHelp.Preview($"{id}-full", valuePos, valueEnd, currentText);

        // Chevron: Tabler IconSelector ("M8 9l4 -4l4 4" + "M16 15l-4 4l-4 -4",
        // 24-grid, stroke 2, round caps) at 14px, opacity .5.
        {
            float iconSpan = 14f * scale;
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
        float popupPadding = Theme.Spacing.Sm * scale;
        const int maxVisibleItems = 10;
        int visibleItems = Math.Min(items.Length, maxVisibleItems);
        float itemSeparator = scale;
        float itemListHeight = visibleItems * height +
                               Math.Max(0, visibleItems - 1) * itemSeparator;
        float popupHeight = itemListHeight + popupPadding * 2;
        float popupY = valueEnd.Y + 2f * scale;
        var displaySize = ImGui.GetIO().DisplaySize;
        if (popupY + popupHeight > displaySize.Y)
        {
            float aboveY = valuePos.Y - popupHeight - 2f * scale;
            popupY = aboveY >= 0 ? aboveY : displaySize.Y - popupHeight;
        }

        // picto CmSelect.module.css (.drop): glass bg, 1px border white@.08,
        // radius 6, padding 4. Outer shadow-panel is NOT rendered — ImGui clips
        // draw commands to the popup window (documented deviation; the drop cell
        // measures the popup rect only).
        var popupBg = resolved.PopupBackground ?? GlassChrome.BackgroundColor;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, rounding);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, popupBg);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 0.08f));

        ImGui.SetNextWindowPos(new Vector2(valuePos.X, popupY));
        ImGui.SetNextWindowSize(new Vector2(totalWidth, popupHeight));
        if (ImGui.BeginPopup(popupId, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar))
        {
            GlassChrome.PrependBlur(ImGui.GetWindowDrawList(), ImGui.GetWindowPos(),
                ImGui.GetWindowPos() + ImGui.GetWindowSize(), rounding);

            float scrollbarSize = 8f * scale;
            bool needsScroll = items.Length > maxVisibleItems;
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, scrollbarSize);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4f * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, itemSeparator));

            // Use the real content region: window padding + border eat into totalWidth,
            // and an oversized child spawns a stray popup scrollbar. The child itself
            // must have zero padding (it inherits the popup's 4px otherwise, overflowing
            // its own frame and offsetting options by 4px).
            // EndChild must run BEFORE EndPopup: a `using var` ImRaii.Child here
            // would dispose (EndChild) at the enclosing block's closing brace,
            // i.e. after EndPopup, producing crossed Begin/End pairing and the
            // "EndPopup on non-popup window" / EndChild-mismatch ImGui asserts.
            var childSize = new Vector2(ImGui.GetContentRegionAvail().X, itemListHeight);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
            bool childOpen = ImGui.BeginChild("##dropdown_scroll", childSize, false,
                needsScroll ? ImGuiWindowFlags.AlwaysVerticalScrollbar : ImGuiWindowFlags.NoScrollbar);
            ImGui.PopStyleVar();
            if (childOpen)
            {
                // Options per CmSelect.module.css (.opt): 26px, padding 0 8, radius 4,
                // 12px text; hover → menu-hover white@.08; current option (.optActive)
                // keeps the same white@.08 fill.
                var optFont = FontRegistry.Resolve(resolved.FontFamily ?? FontFamily.Default, resolved.FontSize ?? 12f);
                bool optFontPushed = optFont is { Available: true };
                if (optFontPushed) optFont!.Push();
                float optPad = 8f * scale;
                float optRounding = 4f * scale;
                uint optFill = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f)));

                for (int i = 0; i < items.Length; i++)
                {
                    bool isSelected = i == selected;
                    var itemPos = ImGui.GetCursorScreenPos();
                    var itemSize = new Vector2(childSize.X - (needsScroll ? scrollbarSize : 0), height);

                    ImGui.PushID(i);
                    if (ImGui.InvisibleButton("##item", itemSize))
                    {
                        if (selected != i || reselectFires)
                        {
                            selected = i; changed = true; onChange?.Invoke(i);
                        }
                        ImGui.CloseCurrentPopup();
                    }
                    bool itemHovered = ImGui.IsItemHovered();

                    var popupDrawList = ImGui.GetWindowDrawList();
                    if (itemHovered || isSelected)
                        popupDrawList.AddRectFilled(itemPos, itemPos + itemSize, optFill, optRounding);

                    string itemDisplay = TruncateText(items[i], itemSize.X - optPad * 2);
                    var itemTextSize = ImGui.CalcTextSize(itemDisplay);
                    var itemTextPos = new Vector2(
                        itemPos.X + optPad,
                        itemPos.Y + (height - itemTextSize.Y) / 2f + scale);
                    popupDrawList.AddText(itemTextPos, ColorEx.ApplyAlpha(Norvrandt.Sheet.CurrentTheme.Text).ToU32(), itemDisplay);
                    if (itemDisplay != items[i] && itemHovered)
                        HoverHelp.Preview($"{id}-item-{i}", itemPos, itemPos + itemSize, items[i]);

                    ImGui.PopID();
                }

                if (optFontPushed) optFont!.Pop();
            }
            ImGui.EndChild();

            ImGui.PopStyleVar(3);
            ImGui.EndPopup();
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

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
