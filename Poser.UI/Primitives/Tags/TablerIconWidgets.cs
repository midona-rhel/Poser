using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Render a Tabler icon at the given size. Tints with <paramref name="color"/>
    /// if provided, otherwise inherits the current theme text color.
    /// </summary>
    public static void Icon(TablerIcon icon, float size, Vector4? color = null, bool flipX = false)
    {
        var doc = Tabler.Get(icon);
        if (doc == null)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }
        var pos = ImGui.GetCursorScreenPos();
        var max = pos + new Vector2(size, size);
        var tint = color ?? Norvrandt.Sheet.CurrentTheme.Text;
        doc.Render(ImGui.GetWindowDrawList(), pos, max, tint, flipX);
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>Render a registered icon by name (custom/project icons included).</summary>
    public static void Icon(string name, float size, Vector4? color = null)
    {
        var doc = Tabler.Get(name);
        if (doc == null)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }
        var pos = ImGui.GetCursorScreenPos();
        var max = pos + new Vector2(size, size);
        var tint = color ?? Norvrandt.Sheet.CurrentTheme.Text;
        doc.Render(ImGui.GetWindowDrawList(), pos, max, tint);
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>
    /// Square button with a centered Tabler icon. Mirrors <see cref="IconButton(Dalamud.Interface.FontAwesomeIcon)"/>
    /// but uses SVG rendering instead of FontAwesome glyphs.
    /// </summary>
    public static bool IconButton(TablerIcon icon)
        => IconButtonTablerCore(icon, default, null, null, null, false, null);
    public static bool IconButton(TablerIcon icon, Action onClick)
        => IconButtonTablerCore(icon, default, null, null, onClick, false, null);
    public static bool IconButton(TablerIcon icon, string tooltip)
        => IconButtonTablerCore(icon, default, null, tooltip, null, false, null);
    public static bool IconButton(TablerIcon icon, string tooltip, Action onClick)
        => IconButtonTablerCore(icon, default, null, tooltip, onClick, false, null);
    public static bool IconButton(TablerIcon icon, in ButtonProps props)
        => IconButtonTablerCore(icon, props.Classes, props.Id, props.Tooltip, props.OnClick, props.Disabled, props.Style, props.FlipX);

    private static bool IconButtonTablerCore(TablerIcon icon, StyleClassSet classes, string? id, string? tooltip,
        Action? onClick, bool disabled, ButtonStyle? inline, bool flipX = false)
    {
        Stylesheet.EnsureInitialized();

        classes = classes + Cls.Icon;
        var pre = Stylesheet.ResolveButton(Cls.Btn + classes, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);
        if (pre.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        var theme = Norvrandt.Sheet.CurrentTheme;
        float side = (pre.Width ?? Sizing.Fixed(theme.RowHeight)).Value * scale;
        float h = (pre.Height ?? Sizing.Fixed(theme.RowHeight)).Value * scale;
        side = SizeUtil.Clamp(side, pre.MinWidth, pre.MaxWidth, scale);
        h = SizeUtil.Clamp(h, pre.MinHeight, pre.MaxHeight, scale);

        string idStr = id ?? Tabler.NameFor(icon);
        var hit = Interactive.Reserve(idStr, new Vector2(side, h), disabled, Norvrandt.AvailableHeight);

        var resolved = Stylesheet.ResolveButton(Cls.Btn + classes, hit.State);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        var elemStyle = resolved.ToElementStyle();
        if (hit.Disabled) elemStyle.Opacity = elemStyle.Opacity ?? 0.4f;
        ChromeBuilder.Paint(hit.ScreenMin, hit.ScreenMax, elemStyle, ChromeBuilder.LiveButtonBg(hit.State));

        // Render the SVG icon centered, ~70% of the button's smaller side.
        var doc = Tabler.Get(icon);
        if (doc != null)
        {
            float iconSize = MathF.Min(side, h) * 0.7f;
            var iconPos = hit.ScreenMin + (new Vector2(side, h) - new Vector2(iconSize, iconSize)) * 0.5f;
            var iconMax = iconPos + new Vector2(iconSize, iconSize);
            var tint = resolved.Color ?? theme.Text;
            if (flipX)
                (iconPos.X, iconMax.X) = (iconMax.X, iconPos.X);
            doc.Render(ImGui.GetWindowDrawList(), iconPos, iconMax, tint);
        }

        if (hit.Hovered && !string.IsNullOrEmpty(tooltip))
            HoverHelp.Explain(id ?? icon.ToString(), hit.ScreenMin, hit.ScreenMax, tooltip!);
        if (hit.Clicked) onClick?.Invoke();
        return hit.Clicked;
    }

    /// <summary>
    /// Two-icon toggle (off / on). Tabler-icon variant of
    /// <see cref="Toggle(string, ref bool, Dalamud.Interface.FontAwesomeIcon, Dalamud.Interface.FontAwesomeIcon)"/>.
    /// </summary>
    public static bool Toggle(string id, ref bool value, TablerIcon iconOff, TablerIcon iconOn, string? tooltip = null)
    {
        Stylesheet.EnsureInitialized();
        var classSet = Cls.Toggle;
        var preState = (value ? PseudoState.On : 0);
        var pre = Stylesheet.ResolveToggle(classSet, preState);
        if (pre.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        float size = (pre.Size ?? Sizing.Fixed(Norvrandt.Sheet.CurrentTheme.RowHeight)).Value * scale;
        size = SizeUtil.Clamp(size, pre.MinSize, pre.MaxSize, scale);

        var hit = Interactive.Reserve(id, new Vector2(size, size), false, Norvrandt.AvailableHeight);
        if (hit.Clicked) value = !value;

        var state = hit.State;
        if (value) state |= PseudoState.On;

        var resolved = Stylesheet.ResolveToggle(classSet, state);
        bool depressed = hit.Active || value;
        var fallbackBg = depressed ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive] : ChromeBuilder.LiveButtonBg(hit.State);
        var elemStyle = resolved.ToElementStyle();
        elemStyle.RaisedGradient = elemStyle.RaisedGradient ?? !depressed;
        ChromeBuilder.Paint(hit.ScreenMin, hit.ScreenMax, elemStyle, fallbackBg);

        var doc = Tabler.Get(value ? iconOn : iconOff);
        if (doc != null)
        {
            float iconSize = size * 0.7f;
            var iconPos = hit.ScreenMin + new Vector2((size - iconSize) * 0.5f, (size - iconSize) * 0.5f);
            doc.Render(ImGui.GetWindowDrawList(), iconPos, iconPos + new Vector2(iconSize, iconSize),
                resolved.Color ?? Norvrandt.Sheet.CurrentTheme.Text);
        }

        if (hit.Hovered && !string.IsNullOrEmpty(tooltip))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, tooltip!);
        return hit.Clicked;
    }

    /// <summary>Single-icon toggle (no on/off chrome). Tabler-icon variant of <see cref="IconToggle"/>.</summary>
    public static bool IconToggle(string id, ref bool value, TablerIcon icon, string? tooltip = null)
    {
        Stylesheet.EnsureInitialized();
        var classSet = Cls.IconToggle;
        var preState = (value ? PseudoState.On : 0);
        var pre = Stylesheet.ResolveIconToggle(classSet, preState);
        if (pre.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        float size = (pre.Size ?? Sizing.Fixed(Norvrandt.Sheet.CurrentTheme.LargeIcon)).Value * scale;
        size = SizeUtil.Clamp(size, pre.MinSize, pre.MaxSize, scale);

        var hit = Interactive.Reserve(id, new Vector2(size, size), false, Norvrandt.AvailableHeight);
        if (hit.Clicked) value = !value;

        var state = hit.State;
        if (value) state |= PseudoState.On;

        var resolved = Stylesheet.ResolveIconToggle(classSet, state);

        var doc = Tabler.Get(icon);
        if (doc != null)
        {
            Vector4 fill;
            if (value)         fill = resolved.OnColor    ?? Norvrandt.Sheet.CurrentTheme.Text;
            else if (hit.Hovered) fill = resolved.HoverColor ?? new Vector4(0.8f, 0.8f, 0.8f, 0.8f);
            else               fill = resolved.OffColor   ?? new Vector4(0.5f, 0.5f, 0.5f, 0.5f);

            doc.Render(ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax, fill);
        }

        if (hit.Hovered && !string.IsNullOrEmpty(tooltip))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, tooltip!);
        return hit.Clicked;
    }
}
