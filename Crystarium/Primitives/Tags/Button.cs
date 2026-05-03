using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    // ---- Short overloads ----

    public static bool Button(string label) => ButtonCore(label, default, null, null, null, false, null);
    public static bool Button(string label, Action onClick) => ButtonCore(label, default, null, null, onClick, false, null);
    public static bool Button(string label, StyleClassSet classes) => ButtonCore(label, classes, null, null, null, false, null);
    public static bool Button(string label, StyleClassSet classes, Action onClick) => ButtonCore(label, classes, null, null, onClick, false, null);
    public static bool Button(string label, in ButtonProps props)
        => ButtonCore(label, props.Classes, props.Id, props.Tooltip, props.OnClick, props.Disabled, props.Style);

    // ---- IconButton overloads ----

    public static bool IconButton(FontAwesomeIcon icon) => IconButtonCore(icon, default, null, null, null, false, null);
    public static bool IconButton(FontAwesomeIcon icon, Action onClick) => IconButtonCore(icon, default, null, null, onClick, false, null);
    public static bool IconButton(FontAwesomeIcon icon, string tooltip) => IconButtonCore(icon, default, null, tooltip, null, false, null);
    public static bool IconButton(FontAwesomeIcon icon, string tooltip, Action onClick) => IconButtonCore(icon, default, null, tooltip, onClick, false, null);
    public static bool IconButton(FontAwesomeIcon icon, in ButtonProps props)
        => IconButtonCore(icon, props.Classes, props.Id, props.Tooltip, props.OnClick, props.Disabled, props.Style);

    // ---- Core ----

    private static bool ButtonCore(string label, StyleClassSet classes, string? id, string? tooltip, Action? onClick, bool disabled, ButtonStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        // Pre-resolve to read width before hit-test (so the rect is right).
        var pre = Stylesheet.ResolveButton(Cls.Btn + classes, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);

        if (pre.Display == UI.Display.None) return false;

        float scale = PoserUI.Scale;
        float height = (pre.Height ?? Sizing.Fixed(Flex.RowHeight)).Value * scale;
        Spacing padding = pre.Padding ?? new Spacing(0, Flex.TextPadding);

        float width;
        if (pre.Width.HasValue && pre.Width.Value.Mode == SizingMode.Fixed)
            width = pre.Width.Value.Value * scale;
        else if (pre.Width.HasValue && pre.Width.Value.Mode == SizingMode.Fill)
            width = ImGui.GetContentRegionAvail().X;
        else
            // Auto / null → fit content
            width = ImGui.CalcTextSize(label).X + padding.Horizontal * scale;

        width = SizeUtil.Clamp(width, pre.MinWidth, pre.MaxWidth, scale);
        height = SizeUtil.Clamp(height, pre.MinHeight, pre.MaxHeight, scale);

        return RenderButton(width, height, padding, label, FontAwesomeIcon.None, false, classes, id ?? label, tooltip, onClick, disabled, inline);
    }

    private static bool IconButtonCore(FontAwesomeIcon icon, StyleClassSet classes, string? id, string? tooltip, Action? onClick, bool disabled, ButtonStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        classes = classes + Cls.Icon;

        var pre = Stylesheet.ResolveButton(Cls.Btn + classes, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);

        if (pre.Display == UI.Display.None) return false;

        float scale = PoserUI.Scale;
        float side = (pre.Width ?? Sizing.Fixed(Flex.RowHeight)).Value * scale;
        side = SizeUtil.Clamp(side, pre.MinWidth, pre.MaxWidth, scale);

        float h = (pre.Height ?? Sizing.Fixed(Flex.RowHeight)).Value * scale;
        h = SizeUtil.Clamp(h, pre.MinHeight, pre.MaxHeight, scale);

        return RenderButton(side, h, pre.Padding ?? new Spacing(0), null, icon, true, classes, id ?? icon.ToIconString(), tooltip, onClick, disabled, inline);
    }

    private static bool RenderButton(float width, float height, Spacing padding,
        string? label, FontAwesomeIcon icon, bool iconOnly,
        StyleClassSet classes, string id, string? tooltip, Action? onClick, bool disabled, ButtonStyle? inline)
    {
        float ambientH = AvailableHeight;
        if (ambientH > height)
        {
            float oy = (ambientH - height) / 2f;
            if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
        }

        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var end = pos + size;

        ImGui.InvisibleButton(id, size);
        bool active = ImGui.IsItemActive() && !disabled;
        bool hovered = ImGui.IsItemHovered() && !disabled;
        bool clicked = ImGui.IsItemClicked() && !disabled;

        // Build state and resolve.
        PseudoState state = PseudoState.None;
        if (hovered)  state |= PseudoState.Hover;
        if (active)   state |= PseudoState.Active;
        if (disabled) state |= PseudoState.Disabled;

        var classSet = Cls.Btn + classes;
        var resolved = Stylesheet.ResolveButton(classSet, state);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        // Live ImGui theme fallback for state-dependent bg.
        Vector4 bg;
        if (resolved.BackgroundColor.HasValue)
        {
            bg = UIColors.ApplyAlpha(resolved.BackgroundColor.Value);
        }
        else
        {
            Vector4 raw = active   ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]
                       : hovered  ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered]
                       :            ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
            bg = UIColors.ApplyAlpha(raw with { W = 1f });
        }
        if (disabled) bg = bg with { W = bg.W * (resolved.Opacity ?? 0.4f) };

        Box(pos, end, new BoxStyle
        {
            BackgroundColor = bg,
            BorderColor = resolved.BorderColor ?? UIColors.Border,
            BorderWidth = resolved.BorderWidth ?? 1f,
            BorderRadius = resolved.BorderRadius ?? 4f,
            BoxShadow = resolved.BoxShadow ?? BoxShadow.Soft(),
            RaisedGradient = resolved.RaisedGradient ?? !active,
        });

        var drawList = ImGui.GetWindowDrawList();
        var textColor = resolved.Color ?? UIColors.Text;
        uint textU32 = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(textColor));

        if (iconOnly)
        {
            var iconFont = UiBuilder.IconFont;
            var iconStr = icon.ToIconString();
            const float iconScale = 0.7f;
            ImGui.PushFont(iconFont);
            var baseIconSize = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();
            var scaledIconSize = baseIconSize * iconScale;
            var iconPos = pos + (size - scaledIconSize) * 0.5f;
            float outlineOffset = 1f * PoserUI.Scale;
            DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr,
                UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset, iconScale);
        }
        else if (label != null)
        {
            var textSize = ImGui.CalcTextSize(label);
            var textPos = pos + (size - textSize) * 0.5f;
            drawList.AddText(textPos, textU32, label);
        }

        if (hovered && !string.IsNullOrEmpty(tooltip)) ImGui.SetTooltip(tooltip);
        if (clicked) onClick?.Invoke();

        return clicked;
    }
}
