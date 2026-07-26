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

    // ---- Measurement ----

    /// <summary>
    /// Measures a text button exactly as it renders: resolved stylesheet
    /// padding and resolved font. Layout code (wrapping, right-alignment)
    /// must use this instead of a hand-authored CalcTextSize + constant
    /// estimate that can drift from the component.
    /// </summary>
    public static Vector2 MeasureButton(string label, StyleClassSet classes = default, bool disabled = false)
    {
        Stylesheet.EnsureInitialized();

        var pre = Stylesheet.ResolveButton(Cls.Btn + classes, disabled ? PseudoState.Disabled : PseudoState.None);
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Norvrandt.Sheet.CurrentTheme;
        float height = (pre.Height ?? Sizing.Fixed(theme.RowHeight)).Value * scale;
        Spacing padding = pre.Padding ?? new Spacing(0, Theme.Spacing.Md);
        float width = MeasureLabel(label, pre).X + padding.Horizontal * scale;
        width  = SizeUtil.Clamp(width,  pre.MinWidth,  pre.MaxWidth,  scale);
        height = SizeUtil.Clamp(height, pre.MinHeight, pre.MaxHeight, scale);
        return new Vector2(width, height);
    }

    /// <summary>Measures the label under the button's resolved stylesheet font.</summary>
    private static Vector2 MeasureLabel(string label, in ButtonStyle resolved)
    {
        var fontHandle = FontRegistry.Resolve(resolved.FontFamily ?? FontFamily.Default, resolved.FontSize ?? 13f);
        bool fontPushed = fontHandle is { Available: true };
        if (fontPushed) fontHandle!.Push();
        var size = ImGui.CalcTextSize(label);
        if (fontPushed) fontHandle!.Pop();
        return size;
    }

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
            width = MeasureLabel(label, pre).X + padding.Horizontal * scale;

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
        // Element opacity (the stylesheet's disabled fade) applies to the
        // button's content — label and icon — as well as its chrome, so a
        // disabled button fades uniformly instead of keeping bright text on
        // a dimmed fill.
        float contentOpacity = elemStyle.Opacity ?? 1f;
        var textColor = resolved.Color ?? Norvrandt.Sheet.CurrentTheme.Text;
        textColor = textColor with { W = textColor.W * contentOpacity };
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
            var iconOutline = Theme.Palette.Black with { W = Theme.Palette.Black.W * contentOpacity };
            var iconFill = Theme.Palette.White with { W = Theme.Palette.White.W * contentOpacity };
            DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr,
                ColorEx.ApplyAlpha(iconOutline.ToU32()), ColorEx.ApplyAlpha(iconFill.ToU32()), outlineOffset, iconScale);
        }
        else if (label != null)
        {
            var fontHandle = FontRegistry.Resolve(resolved.FontFamily ?? FontFamily.Default, resolved.FontSize ?? 13f);
            bool fontPushed = fontHandle is { Available: true };
            if (fontPushed) fontHandle!.Push();
            var textSize = ImGui.CalcTextSize(label);
            // Text labels take the shared button optical baseline; the
            // icon branch above stays independently centred.
            var textPos = hit.ScreenMin + (size - textSize) * 0.5f;
            textPos.Y += Theme.Optical.ButtonText * ImGuiHelpers.GlobalScale;
            drawList.AddText(Theme.Optical.Snap(textPos), textU32, label);
            if (fontPushed) fontHandle!.Pop();
        }

        // A disabled reserve reports Hovered = false, but a disabled action may
        // still explain itself; hover is re-derived geometrically for help.
        bool tooltipHover = hit.Hovered ||
            (hit.Disabled && HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax));
        if (tooltipHover && !string.IsNullOrEmpty(tooltip))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, tooltip!);
        if (hit.Clicked) onClick?.Invoke();

        return hit.Clicked;
    }
}
