using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    // ---- Short overloads ----

    public static bool Checkbox(string id, ref bool value)
        => CheckboxCore(id, ref value, default, null, false, null, null);
    public static bool Checkbox(string id, ref bool value, StyleClassSet classes)
        => CheckboxCore(id, ref value, classes, null, false, null, null);
    public static bool Checkbox(string id, ref bool value, in CheckboxProps props)
        => CheckboxCore(id, ref value, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style);

    public static float CheckboxSize => Flex.ControlSize * PoserUI.Scale;

    private static bool CheckboxCore(string id, ref bool value, StyleClassSet classes,
        string? tooltip, bool disabled, System.Action<bool>? onChange, CheckboxStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.Checkbox + classes;
        var preState = (disabled ? PseudoState.Disabled : PseudoState.None) | (value ? PseudoState.Checked : 0);

        // Resolve once early to read size.
        var pre = Stylesheet.ResolveCheckbox(classSet, preState);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);

        if (pre.Display == UI.Display.None) return false;

        float scale = PoserUI.Scale;
        float size = (pre.Size ?? Sizing.Fixed(Flex.ControlSize)).Value * scale;
        size = SizeUtil.Clamp(size, pre.MinSize, pre.MaxSize, scale);

        // Auto-center within an ambient row cell.
        float ambientH = AvailableHeight;
        if (ambientH > size)
        {
            float oy = (ambientH - size) / 2f;
            if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
        }

        var pos = ImGui.GetCursorScreenPos();
        var end = pos + new Vector2(size, size);

        ImGui.InvisibleButton(id, new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked() && !disabled;
        bool hovered = ImGui.IsItemHovered() && !disabled;
        if (clicked) { value = !value; onChange?.Invoke(value); }

        // Re-resolve with hover/active state.
        var state = preState;
        if (hovered) state |= PseudoState.Hover;
        if (value)   state |= PseudoState.Checked;

        var resolved = Stylesheet.ResolveCheckbox(classSet, state);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        var drawList = ImGui.GetWindowDrawList();
        float rounding = (resolved.BorderRadius ?? 2f) * scale;

        // Background — null-fallback to control bg
        var bg = resolved.BackgroundColor ?? (hovered ? UIColors.ControlBackgroundHovered : UIColors.ControlBackground);
        bg = UIColors.ApplyAlpha(bg);
        if (disabled) bg.W *= resolved.Opacity ?? 0.4f;
        drawList.AddRectFilled(pos, end, ImGui.ColorConvertFloat4ToU32(bg), rounding);

        // Border
        var borderColor = resolved.BorderColor ?? UIColors.Black;
        var borderU = UIColors.ApplyAlpha(borderColor);
        if (disabled) borderU.W *= 0.4f;
        drawList.AddRect(pos, end, ImGui.ColorConvertFloat4ToU32(borderU), rounding, ImDrawFlags.None, (resolved.BorderWidth ?? 1f) * scale);

        // Checkmark
        if (value)
        {
            var iconFont = UiBuilder.IconFont;
            var checkIcon = FontAwesomeIcon.Check.ToIconString();
            ImGui.PushFont(iconFont);
            var iconSize = ImGui.CalcTextSize(checkIcon);
            ImGui.PopFont();
            var iconPos = pos + (new Vector2(size, size) - iconSize) * 0.5f;
            float outlineOffset = 1f * scale;

            uint fill    = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(resolved.CheckmarkColor   ?? UIColors.White));
            uint outline = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(resolved.CheckmarkOutline ?? UIColors.Black));
            if (disabled) { fill = ApplyAlphaU32(fill, 0.4f); outline = ApplyAlphaU32(outline, 0.4f); }

            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, checkIcon, outline, fill, outlineOffset);
        }

        if (hovered && !string.IsNullOrEmpty(tooltip)) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private static uint ApplyAlphaU32(uint c, float mul)
    {
        var v = ImGui.ColorConvertU32ToFloat4(c);
        v.W *= mul;
        return ImGui.ColorConvertFloat4ToU32(v);
    }
}
