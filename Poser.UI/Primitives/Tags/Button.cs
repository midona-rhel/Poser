using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool Button(
        string label,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null)
    {
        float height = ButtonHeight(style);
        float width = ResolveButtonWidth(
            label,
            style,
            ImGui.GetContentRegionAvail().X / ImGuiHelpers.GlobalScale);
        return RenderButton(
            id ?? label,
            new(width, height),
            style,
            disabled,
            help,
            () => DrawButtonLabel(label, style),
            onClick);
    }

    public static bool IconButton(
        FontAwesomeIcon icon,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null)
    {
        var size = IconButtonSize(style);
        return RenderButton(
            id ?? icon.ToIconString(),
            size,
            style,
            disabled,
            help,
            () => DrawFontAwesomeIcon(icon),
            onClick);
    }

    public static bool IconButton(
        TablerIcon icon,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null,
        bool flipX = false)
    {
        var size = IconButtonSize(style);
        return RenderButton(
            id ?? Tabler.NameFor(icon),
            size,
            style,
            disabled,
            help,
            () => DrawTablerIcon(icon, flipX),
            onClick);
    }

    public static Vector2 MeasureButton(string label, ControlStyle style = default)
    {
        float scale = ImGuiHelpers.GlobalScale;
        return new(
            ResolveButtonWidth(
                label,
                style,
                ImGui.GetContentRegionAvail().X / scale) * scale,
            ButtonHeight(style) * scale);
    }

    internal static float IntrinsicButtonWidth(
        string label, ControlStyle style) =>
        MeasureLabel(label, style).X / ImGuiHelpers.GlobalScale
            + ButtonPadding(style) * 2f;

    internal static float ResolveButtonWidth(
        string label, ControlStyle style, float availableWidth) =>
        ControlSizing.Width(
            style.Width,
            IntrinsicButtonWidth(label, style),
            availableWidth);

    internal static bool ButtonAtWidth(
        string label,
        Action? onClick,
        ControlStyle style,
        float width,
        bool disabled,
        string? help,
        string id) =>
        RenderButton(
            id,
            new(width, ButtonHeight(style)),
            style,
            disabled,
            help,
            () => DrawButtonLabel(label, style),
            onClick);

    private static bool RenderButton(
        string id,
        Vector2 logicalSize,
        ControlStyle style,
        bool disabled,
        string? help,
        Action content,
        Action? onClick)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = logicalSize * scale;
        var hit = Interactive.Reserve(id, size, disabled);
        var theme = ActiveTheme;
        float opacity = disabled ? theme.Chrome.ControlDisabledOpacity : 1f;
        var background = style.Bare
            ? (hit.Hovered ? theme.Chrome.WeakOverlay : Vector4.Zero)
            : style.Primary
                ? (hit.Hovered ? theme.Chrome.PrimaryHover : theme.Chrome.Primary)
                : (hit.Hovered ? theme.Chrome.ControlHover : theme.Chrome.ControlFill);
        var border = style.Primary ? background : theme.Chrome.ControlBorder;
        background.W *= opacity;
        border.W *= opacity;

        var draw = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control * scale;
        draw.AddRectFilled(
            hit.ScreenMin,
            hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            radius);
        if (!style.Bare)
        {
            float inset = 0.5f * scale;
            draw.AddRect(
                hit.ScreenMin + new Vector2(inset),
                hit.ScreenMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
                MathF.Max(0f, radius - inset),
                ImDrawFlags.None,
                scale);
        }

        ButtonContent = new(hit.ScreenMin, hit.ScreenMax, opacity);
        content();

        if (!string.IsNullOrEmpty(help) &&
            (hit.Hovered || (hit.Disabled && HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax))))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Clicked)
            onClick?.Invoke();
        return hit.Clicked;
    }

    [ThreadStatic]
    private static ButtonContentBounds ButtonContent;

    private readonly record struct ButtonContentBounds(Vector2 Min, Vector2 Max, float Opacity);

    private static void DrawButtonLabel(string label, ControlStyle style)
    {
        var bounds = ButtonContent;
        var font = FontRegistry.Resolve(
            FontFamily.Default,
            ControlSizing.IsWorkspace(style.Height)
                ? ActiveTheme.Typography.LabelSize
                : ActiveTheme.Typography.BodySize);
        bool pushed = font is { Available: true };
        if (pushed) font!.Push();
        var textSize = ImGui.CalcTextSize(label);
        var position = bounds.Min + (bounds.Max - bounds.Min - textSize) * 0.5f;
        position.Y += ActiveTheme.Optical.ButtonText * ImGuiHelpers.GlobalScale;
        var color = ActiveTheme.Chrome.Text with
        {
            W = ActiveTheme.Chrome.Text.W * bounds.Opacity,
        };
        ImGui.GetWindowDrawList().AddText(
            ActiveTheme.Optical.Snap(position),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)),
            label);
        if (pushed) font!.Pop();
    }

    private static void DrawFontAwesomeIcon(FontAwesomeIcon icon)
    {
        var bounds = ButtonContent;
        var font = UiBuilder.IconFont;
        string glyph = icon.ToIconString();
        float iconScale = ActiveTheme.Controls.IconContentScale;
        ImGui.PushFont(font);
        var baseSize = ImGui.CalcTextSize(glyph);
        ImGui.PopFont();
        var size = baseSize * iconScale;
        var position = bounds.Min + (bounds.Max - bounds.Min - size) * 0.5f;
        float outlineOffset = ImGuiHelpers.GlobalScale;
        var outline = ActiveTheme.Palette.Black with { W = bounds.Opacity };
        var fill = ActiveTheme.Palette.White with { W = bounds.Opacity };
        DrawHelpers.DrawOutlinedIconScaled(
            ImGui.GetWindowDrawList(),
            font,
            position,
            glyph,
            ColorEx.ApplyAlpha(outline.ToU32()),
            ColorEx.ApplyAlpha(fill.ToU32()),
            outlineOffset,
            iconScale);
    }

    private static void DrawTablerIcon(TablerIcon icon, bool flipX)
    {
        var doc = Tabler.Get(icon);
        if (doc == null)
            return;
        var bounds = ButtonContent;
        float side = MathF.Min(
            bounds.Max.X - bounds.Min.X,
            bounds.Max.Y - bounds.Min.Y) * ActiveTheme.Controls.IconContentScale;
        var min = bounds.Min + (bounds.Max - bounds.Min - new Vector2(side)) * 0.5f;
        var max = min + new Vector2(side);
        if (flipX)
            (min.X, max.X) = (max.X, min.X);
        var color = ActiveTheme.Text with { W = ActiveTheme.Text.W * bounds.Opacity };
        doc.Render(ImGui.GetWindowDrawList(), min, max, color);
    }

    private static Vector2 MeasureLabel(string label, ControlStyle style)
    {
        var font = FontRegistry.Resolve(
            FontFamily.Default,
            ControlSizing.IsWorkspace(style.Height)
                ? ActiveTheme.Typography.LabelSize
                : ActiveTheme.Typography.BodySize);
        bool pushed = font is { Available: true };
        if (pushed) font!.Push();
        var result = ImGui.CalcTextSize(label);
        if (pushed) font!.Pop();
        return result;
    }

    private static float ButtonHeight(ControlStyle style) =>
        ControlSizing.Height(style.Height, ActiveTheme.Controls.ComfortableHeight);

    private static Vector2 IconButtonSize(ControlStyle style)
    {
        float height = style.Height.Kind == UiHeightKind.Fixed
            ? style.Height.Value
            : ButtonHeight(style);
        float width = ControlSizing.Width(
            style.Width,
            height,
            ImGui.GetContentRegionAvail().X / ImGuiHelpers.GlobalScale);
        return new(width, height);
    }

    private static float ButtonPadding(ControlStyle style) =>
        ControlSizing.IsWorkspace(style.Height)
            ? ActiveTheme.Spacing.Six
            : ActiveTheme.Spacing.Eight;
}
