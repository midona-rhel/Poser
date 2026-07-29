using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI;

/// <summary>
/// The Picto action-button family (actionButton.module.css): Secondary
/// is <c>.btn</c>, Primary composes <c>.btnPrimary</c>, Danger composes
/// <c>.btnDanger</c>. There is no separate React component — the API is
/// native button behavior plus these composed classes, so the variant
/// is typed rather than a pile of booleans.
/// </summary>
public enum ButtonVariant
{
    Secondary,
    Primary,
    Danger,
}

public static partial class Crystarium
{
    public static bool Button(
        string label,
        Action? onClick = null,
        ButtonVariant variant = ButtonVariant.Secondary,
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
        return RenderTextButton(
            id ?? label,
            label,
            new(width, height),
            variant,
            style,
            disabled,
            help,
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

    public static bool IconButton(
        string icon,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null)
    {
        var size = IconButtonSize(style);
        return RenderButton(
            id ?? icon,
            size,
            style,
            disabled,
            help,
            () => DrawNamedIcon(icon),
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
        MeasureText(label, ButtonLabelStyle(style)).X / ImGuiHelpers.GlobalScale
            + ButtonPadding(style) * 2f;

    internal static float ResolveButtonWidth(
        string label, ControlStyle style, float availableWidth) =>
        ControlSizing.Width(
            style.Width,
            IntrinsicButtonWidth(label, style),
            availableWidth);

    /// <summary>Composition forwarding: the caller resolved the allocated
    /// width; the canonical component still owns everything else.</summary>
    internal static bool ButtonAtWidth(
        string label,
        Action? onClick,
        ControlStyle style,
        float width,
        bool disabled,
        string? help,
        string id,
        ButtonVariant variant = ButtonVariant.Secondary) =>
        RenderTextButton(
            id,
            label,
            new(width, ButtonHeight(style)),
            variant,
            style,
            disabled,
            help,
            onClick);

    // ---- Canonical text button -------------------------------------

    // .btnDanger literals from actionButton.module.css — CSS constants,
    // not theme tokens, identical across every Picto theme.
    private static readonly Vector4 DangerText =
        new(1f, 154f / 255f, 164f / 255f, 1f);            // #ff9aa4
    private static readonly Vector4 DangerBorder =
        new(1f, 71f / 255f, 87f / 255f, 0.35f);           // rgba(255,71,87,.35)
    private static readonly Vector4 DangerFill =
        new(1f, 71f / 255f, 87f / 255f, 0.08f);           // rgba(255,71,87,.08)
    private static readonly Vector4 DangerFillHover =
        new(1f, 71f / 255f, 87f / 255f, 0.15f);           // rgba(255,71,87,.15)

    /// <summary>.btn's <c>transition: background 150ms ease</c> — CSS
    /// `ease` is cubic-bezier(0.25, 0.1, 0.25, 1). Background only; the
    /// border and text switch instantly, exactly like the CSS.</summary>
    private static readonly Transition BackgroundTransition =
        Transition.CubicBezier(0.15f, 0.25f, 0.1f, 0.25f, 1f);

    private sealed class HoverState
    {
        public float Progress;
        public int LastFrame;
    }

    // Component-owned transient hover state keyed by stable ImGui
    // identity (the same seed InvisibleButton hashes).
    private static readonly Dictionary<uint, HoverState> HoverStates = new();

    private static bool RenderTextButton(
        string id,
        string label,
        Vector2 logicalSize,
        ButtonVariant variant,
        ControlStyle style,
        bool disabled,
        string? help,
        Action? onClick)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = logicalSize * scale;
        uint identity = ImGui.GetID(id);
        var hit = Interactive.Reserve(id, size, disabled);
        var theme = ActiveTheme;
        float opacity = disabled ? theme.Chrome.ControlDisabledOpacity : 1f;

        var (fill, fillHover, borderIdle, borderHover, text) = variant switch
        {
            ButtonVariant.Primary => (
                theme.Chrome.Primary,
                theme.Chrome.PrimaryHover,
                theme.Chrome.Primary,
                theme.Chrome.PrimaryHover,
                theme.Palette.White),
            ButtonVariant.Danger => (
                DangerFill,
                DangerFillHover,
                DangerBorder,
                DangerBorder,
                DangerText),
            _ => (
                theme.Chrome.ControlFill,
                theme.Chrome.ControlHover,
                theme.Chrome.ControlBorder,
                theme.Chrome.ControlBorder,
                theme.Chrome.Text),
        };

        // Disabled buttons take no hover styling; the 150ms background
        // transition follows hover for everything else.
        float eased = AdvanceHover(identity, hit.Hovered && !disabled);
        var background = Vector4.Lerp(fill, fillHover, eased);
        var border = hit.Hovered && !disabled ? borderHover : borderIdle;
        background.W *= opacity;
        border.W *= opacity;

        var draw = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control * scale;
        draw.AddRectFilled(
            hit.ScreenMin,
            hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            radius);
        float inset = 0.5f * scale;
        draw.AddRect(
            hit.ScreenMin + new Vector2(inset),
            hit.ScreenMax - new Vector2(inset),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
            MathF.Max(0f, radius - inset),
            ImDrawFlags.None,
            scale);

        // .btn:focus-visible — 2px primary-60 outline offset 1px, shown
        // for keyboard focus only; pointer interaction never invents one.
        if (hit.Focused && Interactive.KeyboardNavActive)
        {
            float offset = 1f * scale;
            float thickness = 2f * scale;
            float expand = offset + thickness * 0.5f;
            draw.AddRect(
                hit.ScreenMin - new Vector2(expand),
                hit.ScreenMax + new Vector2(expand),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Chrome.PrimaryHover)),
                radius + expand,
                ImDrawFlags.None,
                thickness);
        }

        // Centered label through the canonical text path, clipped to the
        // button's visual bounds.
        var labelStyle = ButtonLabelStyle(
            style, text with { W = text.W * opacity });
        var measured = MeasureText(label, labelStyle);
        var position = hit.ScreenMin + (hit.Size - measured) * 0.5f;
        draw.PushClipRect(hit.ScreenMin, hit.ScreenMax, true);
        try
        {
            TextAt(position, label, labelStyle);
        }
        finally
        {
            draw.PopClipRect();
        }

        if (!string.IsNullOrEmpty(help) &&
            (hit.Hovered || (hit.Disabled && HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax))))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
            onClick?.Invoke();
        return hit.Activated;
    }

    private static TextStyle ButtonLabelStyle(
        ControlStyle style, Vector4? color = null) => new()
    {
        Size = ControlSizing.IsWorkspace(style.Height)
            ? ActiveTheme.Typography.LabelSize
            : ActiveTheme.Typography.BodySize,
        Color = color,
    };

    private static float AdvanceHover(uint identity, bool hovered)
    {
        int frame = ImGui.GetFrameCount();
        if (!HoverStates.TryGetValue(identity, out var state))
        {
            if (HoverStates.Count > 512)
                PruneHoverStates(frame);
            state = new HoverState { Progress = hovered ? 1f : 0f };
            HoverStates[identity] = state;
        }
        float step = BackgroundTransition.DurationSeconds > 0f
            ? ImGui.GetIO().DeltaTime / BackgroundTransition.DurationSeconds
            : 1f;
        state.Progress = Math.Clamp(
            state.Progress + (hovered ? step : -step), 0f, 1f);
        state.LastFrame = frame;
        return BackgroundTransition.Evaluate(state.Progress);
    }

    private static void PruneHoverStates(int frame)
    {
        var stale = new List<uint>();
        foreach (var (key, value) in HoverStates)
            if (frame - value.LastFrame > 2)
                stale.Add(key);
        foreach (var key in stale)
            HoverStates.Remove(key);
    }

    // ---- Icon buttons (slice 4 owns their conformance) --------------

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
        var background = style.Selected
            ? theme.Chrome.SegmentSelected
            : style.Bare
            ? (hit.Hovered ? theme.Chrome.WeakOverlay : Vector4.Zero)
            : (hit.Hovered ? theme.Chrome.ControlHover : theme.Chrome.ControlFill);
        var border = theme.Chrome.ControlBorder;
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
        if (style.Slashed)
        {
            float inset = ActiveTheme.Spacing.Two * scale;
            draw.AddLine(
                hit.ScreenMin + new Vector2(inset),
                hit.ScreenMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(ActiveTheme.TextDim)),
                scale);
        }

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
        var bounds = ButtonContent;
        IconIn(
            bounds.Min, bounds.Max, icon,
            contentScale: ActiveTheme.Controls.IconContentScale,
            opacity: bounds.Opacity,
            flipX: flipX);
    }

    private static void DrawNamedIcon(string icon)
    {
        var bounds = ButtonContent;
        IconIn(
            bounds.Min, bounds.Max, icon,
            contentScale: ActiveTheme.Controls.IconContentScale,
            opacity: bounds.Opacity);
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
