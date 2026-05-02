using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    // ---- Toggle (two-icon) ----

    public static bool Toggle(string id, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn)
        => ToggleCore(id, ref value, iconOff, iconOn, default, null, false, null, null);
    public static bool Toggle(string id, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn, string tooltip)
        => ToggleCore(id, ref value, iconOff, iconOn, default, tooltip, false, null, null);
    public static bool Toggle(string id, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn, in ToggleProps props)
        => ToggleCore(id, ref value, iconOff, iconOn, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style);

    public static float ToggleSize => Flex.RowHeight * PoserUI.Scale;

    private static bool ToggleCore(string id, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn,
        StyleClassSet classes, string? tooltip, bool disabled, Action<bool>? onChange, ToggleStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.Toggle + classes;
        var preState = (disabled ? PseudoState.Disabled : PseudoState.None) | (value ? PseudoState.On : 0);
        var pre = Stylesheet.ResolveToggle(classSet, preState);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);

        float scale = PoserUI.Scale;
        float size = (pre.Size ?? Sizing.Fixed(Flex.RowHeight)).Value * scale;

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
        bool active = ImGui.IsItemActive() && !disabled;
        if (clicked) { value = !value; onChange?.Invoke(value); }

        var state = preState;
        if (hovered) state |= PseudoState.Hover;
        if (active)  state |= PseudoState.Active;
        if (value)   state |= PseudoState.On;

        var resolved = Stylesheet.ResolveToggle(classSet, state);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        // Treat on / active as the "depressed" state for chrome.
        bool depressed = active || value;

        Vector4 bg;
        if (resolved.BackgroundColor.HasValue)
        {
            bg = UIColors.ApplyAlpha(resolved.BackgroundColor.Value);
        }
        else
        {
            Vector4 raw = depressed ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]
                       : hovered  ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered]
                       :            ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
            bg = UIColors.ApplyAlpha(raw with { W = 1f });
        }
        if (disabled) bg.W *= resolved.Opacity ?? 0.4f;

        Box(pos, end, new BoxStyle
        {
            BackgroundColor = bg,
            BorderColor = resolved.BorderColor ?? UIColors.Border,
            BorderWidth = resolved.BorderWidth ?? 1f,
            BorderRadius = resolved.BorderRadius ?? 4f,
            BoxShadow = resolved.BoxShadow ?? BoxShadow.Soft(),
            RaisedGradient = resolved.RaisedGradient ?? !depressed,
        });

        // Icon
        var drawList = ImGui.GetWindowDrawList();
        var iconFont = UiBuilder.IconFont;
        var iconStr = (value ? iconOn : iconOff).ToIconString();
        const float iconScale = 0.7f;

        ImGui.PushFont(iconFont);
        var iconSize = ImGui.CalcTextSize(iconStr);
        float fontSize = ImGui.GetFontSize();
        ImGui.PopFont();

        var iconPos = pos + new Vector2(
            (size - iconSize.X * iconScale) * 0.5f,
            (size - fontSize * iconScale) * 0.5f);
        float outlineOffset = 1f * scale;
        DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr,
            UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset, iconScale);

        if (hovered && !string.IsNullOrEmpty(tooltip)) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    // ---- IconToggle (single icon, no chrome) ----

    public static bool IconToggle(string id, ref bool value, FontAwesomeIcon icon)
        => IconToggleCore(id, ref value, icon, default, null, false, null, null);
    public static bool IconToggle(string id, ref bool value, FontAwesomeIcon icon, string tooltip)
        => IconToggleCore(id, ref value, icon, default, tooltip, false, null, null);
    public static bool IconToggle(string id, ref bool value, FontAwesomeIcon icon, in IconToggleProps props)
        => IconToggleCore(id, ref value, icon, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style);

    public static float IconToggleSize => Flex.LargeIconSize * PoserUI.Scale;

    private static bool IconToggleCore(string id, ref bool value, FontAwesomeIcon icon,
        StyleClassSet classes, string? tooltip, bool disabled, Action<bool>? onChange, IconToggleStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.IconToggle + classes;
        var preState = (disabled ? PseudoState.Disabled : PseudoState.None) | (value ? PseudoState.On : 0);
        var pre = Stylesheet.ResolveIconToggle(classSet, preState);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);

        float scale = PoserUI.Scale;
        float size = (pre.Size ?? Sizing.Fixed(Flex.LargeIconSize)).Value * scale;

        float ambientH = AvailableHeight;
        if (ambientH > size)
        {
            float oy = (ambientH - size) / 2f;
            if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
        }

        var pos = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked() && !disabled;
        bool hovered = ImGui.IsItemHovered() && !disabled;
        if (clicked) { value = !value; onChange?.Invoke(value); }

        var state = preState;
        if (hovered) state |= PseudoState.Hover;
        if (value)   state |= PseudoState.On;

        var resolved = Stylesheet.ResolveIconToggle(classSet, state);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        var iconFont = UiBuilder.IconFont;
        var iconStr = icon.ToIconString();
        ImGui.PushFont(iconFont);
        var iconTextSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var iconPos = pos + new Vector2((size - iconTextSize.X) * 0.5f, (size - iconTextSize.Y) * 0.5f);
        float outlineOffset = 1f * scale;

        var drawList = ImGui.GetWindowDrawList();
        Vector4 outline = resolved.OutlineColor ?? UIColors.Black;

        Vector4 fill;
        if (value)         fill = resolved.OnColor    ?? UIColors.White;
        else if (hovered)  fill = resolved.HoverColor ?? new Vector4(0.8f, 0.8f, 0.8f, 0.8f);
        else               fill = resolved.OffColor   ?? new Vector4(0.5f, 0.5f, 0.5f, 0.5f);

        DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, iconStr,
            ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(outline)),
            ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(fill)),
            outlineOffset);

        if (hovered && !string.IsNullOrEmpty(tooltip)) ImGui.SetTooltip(tooltip);
        return clicked;
    }
}
