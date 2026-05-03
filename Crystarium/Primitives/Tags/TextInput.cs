using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool TextInput(string id, ref string value)
        => TextInputCore(id, ref value, null, default, null, false, null, null);
    public static bool TextInput(string id, ref string value, string placeholder)
        => TextInputCore(id, ref value, placeholder, default, null, false, null, null);
    public static bool TextInput(string id, ref string value, in TextInputProps props)
        => TextInputCore(id, ref value, props.Placeholder, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style);

    private static bool TextInputCore(string id, ref string value, string? placeholder,
        StyleClassSet classes, string? tooltip, bool disabled, Action<string>? onChange, TextInputStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.TextInput + classes;
        var preState = disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.ResolveTextInput(classSet, preState);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        if (resolved.Display == UI.Display.None) return false;

        float scale = PoserUI.Scale;
        float height = (resolved.Height ?? Sizing.Fixed(Flex.RowHeight)).Value * scale;
        height = SizeUtil.Clamp(height, resolved.MinHeight, resolved.MaxHeight, scale);
        float widthPx;
        if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            widthPx = resolved.Width.Value.Value * scale;
        else
            widthPx = AvailableWidth;
        widthPx = SizeUtil.Clamp(widthPx, resolved.MinWidth, resolved.MaxWidth, scale);

        var bg = resolved.BackgroundColor ?? UIColors.ControlBackground;
        var border = resolved.BorderColor ?? UIColors.Border;
        var pad = resolved.Padding ?? new Spacing(0, Flex.TextPadding);
        float framePadX = pad.Left * scale;
        float framePadY = (height - ImGui.GetTextLineHeight()) / 2f;

        ImGui.PushStyleColor(ImGuiCol.FrameBg, bg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, bg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, bg);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(framePadX, framePadY));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, (resolved.BorderRadius ?? 3f) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, resolved.BorderWidth ?? 1f);

        ImGui.SetNextItemWidth(widthPx);
        bool changed;
        if (placeholder != null)
            changed = ImGui.InputTextWithHint(id, placeholder, ref value);
        else
            changed = ImGui.InputText(id, ref value);

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(4);

        // Post-draw :focus chrome — overlay an accent outline if the input has keyboard focus.
        if (ImGui.IsItemFocused() || ImGui.IsItemActive())
        {
            var focusedStyle = Stylesheet.ResolveTextInput(classSet, id, preState | PseudoState.Focus);
            if (inline.HasValue) focusedStyle = focusedStyle.MergedWith(inline.Value);
            if (focusedStyle.BorderColor.HasValue)
            {
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                var focusBorder = UIColors.ApplyAlpha(focusedStyle.BorderColor.Value);
                var radiusPx = (focusedStyle.BorderRadius ?? resolved.BorderRadius ?? 3f) * scale;
                ImGui.GetWindowDrawList().AddRect(rectMin, rectMax,
                    ImGui.ColorConvertFloat4ToU32(focusBorder),
                    radiusPx, ImDrawFlags.None,
                    (focusedStyle.BorderWidth ?? 1f) * scale);
            }
        }

        if (changed) onChange?.Invoke(value);
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        return changed;
    }
}
