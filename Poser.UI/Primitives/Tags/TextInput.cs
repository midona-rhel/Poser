using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool TextInput(string id, ref string value)
        => TextInputCore(id, ref value, null, default, null, false, false, null, null);
    public static bool TextInput(string id, ref string value, string placeholder)
        => TextInputCore(id, ref value, placeholder, default, null, false, false, null, null);
    public static bool TextInput(string id, ref string value, in TextInputProps props)
        => TextInputCore(id, ref value, props.Placeholder, props.Classes, props.Tooltip, props.Disabled, props.Clearable, props.OnChange, props.Style);

    private static bool TextInputCore(string id, ref string value, string? placeholder,
        StyleClassSet classes, string? tooltip, bool disabled, bool clearable, Action<string>? onChange, TextInputStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.TextInput + classes;
        var preState = disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.ResolveTextInput(classSet, preState);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        if (resolved.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        float height = (resolved.Height ?? Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight)).Value * scale;
        height = SizeUtil.Clamp(height, resolved.MinHeight, resolved.MaxHeight, scale);
        float widthPx;
        if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            widthPx = resolved.Width.Value.Value * scale;
        else
            widthPx = Norvrandt.AvailableWidth;
        widthPx = SizeUtil.Clamp(widthPx, resolved.MinWidth, resolved.MaxWidth, scale);

        var bg = resolved.BackgroundColor ?? Crystarium.ActiveTheme.SurfaceSunken;
        var border = resolved.BorderColor ?? Crystarium.ActiveTheme.Border;
        var pad = resolved.Padding ?? new Spacing(0, Crystarium.ActiveTheme.Page.ActionGap);
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

        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        var cursorAfterInput = ImGui.GetCursorScreenPos();
        bool inputFocused = ImGui.IsItemFocused() || ImGui.IsItemActive();
        bool inputHovered = ImGui.IsItemHovered();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(4);

        // Post-draw :focus chrome — overlay an accent outline if the input has keyboard focus.
        if (inputFocused)
        {
            var focusedStyle = Stylesheet.ResolveTextInput(classSet, id, preState | PseudoState.Focus);
            if (inline.HasValue) focusedStyle = focusedStyle.MergedWith(inline.Value);
            if (focusedStyle.BorderColor.HasValue)
            {
                var focusBorder = ColorEx.ApplyAlpha(focusedStyle.BorderColor.Value);
                var radiusPx = (focusedStyle.BorderRadius ?? resolved.BorderRadius ?? 3f) * scale;
                ImGui.GetWindowDrawList().AddRect(inputMin, inputMax,
                    ImGui.ColorConvertFloat4ToU32(focusBorder),
                    radiusPx, ImDrawFlags.None,
                    (focusedStyle.BorderWidth ?? 1f) * scale);
            }
        }

        if (clearable && !disabled && value.Length > 0)
        {
            var center = new Vector2(inputMax.X - 13f * scale, (inputMin.Y + inputMax.Y) * 0.5f);
            var hitPadding = new Vector2(9f, 9f) * scale;
            bool clearHovered = ImGui.IsWindowHovered() &&
                                ImGui.IsMouseHoveringRect(center - hitPadding, center + hitPadding);

            var drawList = ImGui.GetWindowDrawList();
            uint circle = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                Crystarium.ActiveTheme.Text with { W = clearHovered ? 0.62f : 0.42f }));
            drawList.AddCircleFilled(center, 7f * scale, circle, 20);

            float iconSize = 9f * scale;
            ImGui.SetCursorScreenPos(center - new Vector2(iconSize * 0.5f));
            Icon(TablerIcon.X, iconSize,
                ColorEx.ApplyAlpha(Crystarium.ActiveTheme.SurfaceSunken with { W = 1f }));
            ImGui.SetCursorScreenPos(cursorAfterInput);

            if (clearHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    value = "";
                    changed = true;
                }
            }
        }

        if (changed) onChange?.Invoke(value);
        if (!string.IsNullOrEmpty(tooltip) && inputHovered)
            HoverHelp.Explain(id, inputMin, inputMax, tooltip!);

        return changed;
    }
}
