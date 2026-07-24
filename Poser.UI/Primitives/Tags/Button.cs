using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
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

        var pre = Stylesheet.ResolveButton(Cls.Btn + classes, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);
        if (pre.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        var theme = Norvrandt.Sheet.CurrentTheme;
        float height = (pre.Height ?? Sizing.Fixed(theme.RowHeight)).Value * scale;
        Spacing padding = pre.Padding ?? new Spacing(0, Theme.Spacing.Md);

        float width;
        if (pre.Width.HasValue && pre.Width.Value.Mode == SizingMode.Fixed)
            width = pre.Width.Value.Value * scale;
        else if (pre.Width.HasValue && pre.Width.Value.Mode == SizingMode.Fill)
            width = ImGui.GetContentRegionAvail().X;
        else
            width = ImGui.CalcTextSize(label).X + padding.Horizontal * scale;

        width  = SizeUtil.Clamp(width,  pre.MinWidth,  pre.MaxWidth,  scale);
        height = SizeUtil.Clamp(height, pre.MinHeight, pre.MaxHeight, scale);

        return RenderButton(width, height, label, FontAwesomeIcon.None, false, classes, id ?? label, tooltip, onClick, disabled, inline);
    }

    private static bool IconButtonCore(FontAwesomeIcon icon, StyleClassSet classes, string? id, string? tooltip, Action? onClick, bool disabled, ButtonStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        classes = classes + Cls.Icon;

        var pre = Stylesheet.ResolveButton(Cls.Btn + classes, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);
        if (pre.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        var theme = Norvrandt.Sheet.CurrentTheme;
        float side = (pre.Width  ?? Sizing.Fixed(theme.RowHeight)).Value * scale;
        float h    = (pre.Height ?? Sizing.Fixed(theme.RowHeight)).Value * scale;
        side = SizeUtil.Clamp(side, pre.MinWidth,  pre.MaxWidth,  scale);
        h    = SizeUtil.Clamp(h,    pre.MinHeight, pre.MaxHeight, scale);

        return RenderButton(side, h, null, icon, true, classes, id ?? icon.ToIconString(), tooltip, onClick, disabled, inline);
    }

    private static bool RenderButton(float width, float height,
        string? label, FontAwesomeIcon icon, bool iconOnly,
        StyleClassSet classes, string id, string? tooltip, Action? onClick, bool disabled, ButtonStyle? inline)
    {
        var hit = Interactive.Reserve(id, new Vector2(width, height), disabled, Norvrandt.AvailableHeight);

        var resolved = Stylesheet.ResolveButton(Cls.Btn + classes, hit.State);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        var elemStyle = resolved.ToElementStyle();
        if (hit.Disabled) elemStyle.Opacity = elemStyle.Opacity ?? 0.4f;
        ChromeBuilder.Paint(hit.ScreenMin, hit.ScreenMax, elemStyle, ChromeBuilder.LiveButtonBg(hit.State));

        var drawList = ImGui.GetWindowDrawList();
        var textColor = resolved.Color ?? Norvrandt.Sheet.CurrentTheme.Text;
        uint textU32 = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(textColor));
        var size = hit.Size;

        if (iconOnly)
        {
            var iconFont = UiBuilder.IconFont;
            var iconStr = icon.ToIconString();
            const float iconScale = 0.7f;
            ImGui.PushFont(iconFont);
            var baseIconSize = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();
            var scaledIconSize = baseIconSize * iconScale;
            var iconPos = hit.ScreenMin + (size - scaledIconSize) * 0.5f;
            float outlineOffset = 1f * ImGuiHelpers.GlobalScale;
            DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr,
                ColorEx.ApplyAlpha(Theme.Palette.Black.ToU32()), ColorEx.ApplyAlpha(Theme.Palette.White.ToU32()), outlineOffset, iconScale);
        }
        else if (label != null)
        {
            var textSize = ImGui.CalcTextSize(label);
            var textPos = hit.ScreenMin + (size - textSize) * 0.5f;
            drawList.AddText(textPos, textU32, label);
        }

        if (hit.Hovered && !string.IsNullOrEmpty(tooltip)) ImGui.SetTooltip(tooltip);
        if (hit.Clicked) onClick?.Invoke();

        return hit.Clicked;
    }
}
