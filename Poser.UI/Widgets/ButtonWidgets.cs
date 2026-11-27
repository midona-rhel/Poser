using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class ButtonWidgets
{
    /// <summary>Bottom-left screen corner of the text or icon button currently
    /// invoking its callback. Use it to anchor a popup to that button. Outside
    /// those callbacks it retains the last value; temporary toggles do not set it.</summary>

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
        TablerIcon icon,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null,
        bool flipX = false,
        float iconSize = 16f,
        float strokeWidth = 1.5f,
        bool disruptive = false)
    {
        var size = IconButtonSize(style);
        return RenderIconButton(
            id ?? Tabler.NameFor(icon),
            size,
            style.Selected,
            disabled,
            help,
            (min, max, opacity, background) => DrawButtonIcon(
                min, max, icon, iconSize, opacity, background, flipX,
                strokeWidth, filled: style.Selected),
            onClick,
            disruptive);
    }

    /// <summary>Draws a momentary button using a registered icon name.</summary>
    public static bool IconButton(
        string icon,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null,
        bool flipX = false,
        float iconSize = 16f,
        float strokeWidth = 1.5f)
    {
        var size = IconButtonSize(style);
        return RenderIconButton(
            id ?? icon,
            size,
            style.Selected,
            disabled,
            help,
            (min, max, opacity, background) => DrawButtonIcon(
                min, max, icon, iconSize, opacity, background, flipX,
                strokeWidth, filled: style.Selected),
            onClick);
    }

    /// <summary>Draws an icon that switches on and off, optionally crossed out or dimmed.</summary>
    public static bool TemporaryIconToggle(
        TablerIcon icon,
        bool selected,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null,
        bool slashed = false,
        bool flipX = false,
        bool dimmed = false)
    {
        var size = IconButtonSize(style);
        return RenderTemporaryIconToggle(
            id ?? Tabler.NameFor(icon),
            size,
            selected,
            slashed,
            disabled,
            help,
            (min, max, opacity) => DrawLegacyButtonIcon(
                min, max, icon, opacity, flipX),
            onClick,
            dimmed);
    }

    /// <summary>The same on/off icon, using a registered icon name.</summary>
    public static bool TemporaryIconToggle(
        string icon,
        bool selected,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null,
        bool slashed = false,
        bool flipX = false)
    {
        var size = IconButtonSize(style);
        return RenderTemporaryIconToggle(
            id ?? icon,
            size,
            selected,
            slashed,
            disabled,
            help,
            (min, max, opacity) => DrawLegacyButtonIcon(
                min, max, icon, opacity, flipX),
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

    /// <summary>The width the button needs: the label's width, plus the
    /// padding and the 1px border on each side.</summary>
    internal static float IntrinsicButtonWidth(
        string label, ControlStyle style) =>
        MeasureText(label, ButtonLabelStyle(style)).X / ImGuiHelpers.GlobalScale
            + ButtonPadding(style) * 2f
            + 2f;

    internal static float ResolveButtonWidth(
        string label, ControlStyle style, float availableWidth) =>
        ControlSizing.Width(
            style,
            IntrinsicButtonWidth(label, style),
            availableWidth);

    /// <summary>Draws a button at a width the caller has already worked out.
    /// Everything else is drawn as for any button.</summary>
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

    // ---- Text button -----------------------------------------------

    // The Danger button's colours. They are fixed rather than taken from
    // the theme, because they are the same in every theme.
    private static readonly Vector4 DangerText =
        new(1f, 154f / 255f, 164f / 255f, 1f);            // #ff9aa4
    private static readonly Vector4 DangerBorder =
        new(1f, 71f / 255f, 87f / 255f, 0.35f);           // rgba(255,71,87,.35)
    private static readonly Vector4 DangerFill =
        new(1f, 71f / 255f, 87f / 255f, 0.08f);           // rgba(255,71,87,.08)
    private static readonly Vector4 DangerFillHover =
        new(1f, 71f / 255f, 87f / 255f, 0.15f);           // rgba(255,71,87,.15)

    /// <summary>On hover the background fades over 150 ms with a standard
    /// ease curve. Only the background fades; the border and text change at
    /// once.</summary>
    private static readonly Transition BackgroundTransition =
        Transition.CubicBezier(0.15f, 0.25f, 0.1f, 0.25f, 1f);

    // Animation slots for the icon button. Animations are stored by the
    // button's id. The icon button's background and opacity share one timer
    // under that id. The text button's hover fade is the only animation
    // under its id, so it needs no slot number.
    private const int IconBackgroundChannel = 0;
    private const int IconOpacityChannel = 1;

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
        disabled |= Interactive.GreyedOut;
        var hit = Interactive.Reserve(id, size, disabled);
        PaintTextButton(hit, identity, label, style, variant, disabled);

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
        {
            onClick?.Invoke();
        }
        return hit.Activated;
    }

    /// <summary>
    /// Draws the whole text button. The caller only places it, shows its
    /// help text and handles the click.
    /// </summary>
    private static void PaintTextButton(
        in InteractionResult hit,
        uint identity,
        string label,
        ControlStyle style,
        ButtonVariant variant,
        bool disabled)
    {
        var labelColor = PaintTextButtonBox(hit, identity, variant, disabled);
        DrawButtonLabelClipped(
            ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax,
            label, style, labelColor);
    }

    /// <summary>
    /// Draws the button's box: background, border, and the faded look when
    /// disabled. Returns the colour for the label, so the label always
    /// matches the box.
    /// </summary>
    private static Vector4 PaintTextButtonBox(
        in InteractionResult hit,
        uint identity,
        ButtonVariant variant,
        bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
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
            ButtonVariant.Disruptive => (
                theme.Chrome.Disruptive with { W = 0.08f },
                theme.Chrome.Disruptive with { W = 0.15f },
                theme.Chrome.Disruptive with { W = 0.35f },
                theme.Chrome.Disruptive with { W = 0.35f },
                theme.Chrome.Disruptive),
            _ => (
                theme.Chrome.ControlFill,
                theme.Chrome.ControlHover,
                theme.Chrome.ControlBorder,
                theme.Chrome.ControlBorder,
                theme.Chrome.Text),
        };

        var draw = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control * scale;
        float borderPx = 1f * scale;
        float inset = 0.5f * scale;
        // The hover fade updates every frame, and a disabled button fades
        // back to normal. So a button disabled while hovered and enabled
        // again away from the pointer does not show an old hover colour.
        float eased = BackgroundTransition.Evaluate(
            Motion.Progress(
                identity,
                hit.Hovered && !disabled,
                BackgroundTransition.DurationSeconds));
        if (disabled)
        {
            // A disabled button fades as one piece: background, border and
            // label together, as if drawn first and then faded once.
            // ControlPaint.DisabledGroup draws that and returns the label
            // colour that gives the same look. For see-through fills the
            // result is exact. For a solid fill (Primary) it is exact over
            // the page background and slightly off over anything else.
            var content = ControlPaint.DisabledGroup(
                draw, hit.ScreenMin, hit.ScreenMax,
                radius, borderPx, fill, borderIdle, opacity);
            return content.Label(text);
        }
        else
        {
            // Enabled: the border is drawn over the fill. The background
            // fades between its normal and hover colours over 150 ms,
            // mixing them with their transparency taken into account, as
            // web browsers do.
            var background = ColorEx.PremultipliedLerp(fill, fillHover, eased);
            var border = hit.Hovered ? borderHover : borderIdle;
            draw.AddRectFilled(
                hit.ScreenMin,
                hit.ScreenMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
                radius);
            draw.AddRect(
                hit.ScreenMin + new Vector2(inset),
                hit.ScreenMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
                MathF.Max(0f, radius - inset),
                ImDrawFlags.None,
                borderPx);
        }

        // Keyboard activation is supported without drawing a focus outline.
        return text;
    }

    private static TextStyle ButtonLabelStyle(
        ControlStyle style, Vector4? color = null) => new()
    {
        Size = ControlSizing.IsWorkspace(style.Height)
            ? ActiveTheme.Typography.LabelSize
            : ActiveTheme.Typography.BodySize,
        Color = color,
    };

    /// <summary>Draws the label centred, cut off at the button's
    /// edges.</summary>
    private static void DrawButtonLabelClipped(
        ImDrawListPtr draw, Vector2 min, Vector2 max,
        string label, ControlStyle style, Vector4 color)
    {
        var labelStyle = ButtonLabelStyle(style, color);
        draw.PushClipRect(min, max, true);
        try
        {
            TextInBand(min, max - min, label, labelStyle, TextAlign.Center);
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    // ---- Icon button -----------------------------------------------

    // The icon button's background and opacity fade over 150 ms with the
    // design's default ease curve.
    private static readonly Transition IconButtonTransition =
        Transition.CubicBezier(0.15f, 0.4f, 0f, 0.22f, 1f);

    /// <summary>How faded a disabled icon button is. It is fixed rather than
    /// taken from the theme, and every disabled icon button uses this
    /// value.</summary>
    private const float IconButtonDisabledOpacity = 0.2f;

    private static bool RenderIconButton(
        string id,
        Vector2 logicalSize,
        bool selected,
        bool disabled,
        string? help,
        Action<Vector2, Vector2, float, Vector4> content,
        Action? onClick,
        bool disruptive = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = logicalSize * scale;
        uint identity = ImGui.GetID(id);
        disabled |= Interactive.GreyedOut;
        var hit = Interactive.Reserve(
            id, size, disabled, activateOnSpace: true);
        var theme = ActiveTheme;
        var targetBackground = selected
            ? theme.Chrome.SegmentSelected
            : hit.Active
                ? theme.Chrome.ActiveOverlay
                : hit.Hovered
                    ? theme.Chrome.WeakOverlay
                    : Vector4.Zero;
        // Hover and selection restore full opacity; a held press changes the
        // background. Selected icons also request their filled variant.
        float targetOpacity = selected || hit.Hovered ? 1f : 0.8f;
        // The background and the opacity share one timer, so pressing a
        // button that is still fading in restarts both together.
        Span<MotionChannel> visual =
        [
            MotionChannel.Color(
                IconBackgroundChannel,
                disabled ? Vector4.Zero : targetBackground),
            MotionChannel.Number(
                IconOpacityChannel,
                disabled ? 0.8f : targetOpacity),
        ];
        Motion.Toward(identity, IconButtonTransition, visual);
        var background = visual[0].Value;
        float opacity = visual[1].Scalar;
        if (disabled)
        {
            background = Vector4.Zero;
            opacity = IconButtonDisabledOpacity;
        }

        var draw = ImGui.GetWindowDrawList();
        // Icon buttons have 5px rounded corners, not the 6px of buttons
        // with a border.
        float radius = 5f * scale;
        var fadedBackground = background.Fade(opacity);
        draw.PushClipRect(hit.ScreenMin, hit.ScreenMax, true);
        try
        {
            draw.AddRectFilled(
                hit.ScreenMin,
                hit.ScreenMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(fadedBackground)),
                radius);

            content(hit.ScreenMin, hit.ScreenMax, opacity, background);
            // A disruptive verb wears the purple outline, as a button does.
            if (disruptive)
                draw.AddRect(
                    hit.ScreenMin + new Vector2(0.5f),
                    hit.ScreenMax - new Vector2(0.5f),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                        theme.Chrome.Disruptive.Fade(disabled ? theme.Chrome.DisabledOpacity : 1f))),
                    radius, ImDrawFlags.RoundCornersAll, 1.5f * scale);
        }
        finally
        {
            draw.PopClipRect();
        }

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
        {
            onClick?.Invoke();
        }
        return hit.Activated;
    }

    private static void DrawButtonIcon(
        Vector2 min,
        Vector2 max,
        TablerIcon icon,
        float logicalSize,
        float opacity,
        Vector4 background,
        bool flipX,
        float strokeWidth,
        bool filled = false)
    {
        var (iconMin, iconMax) = CenteredIconBounds(
            min, max, logicalSize);
        IconInComposited(
            iconMin, iconMax, icon,
            opacity: opacity,
            background: background,
            flipX: flipX,
            strokeWidth: strokeWidth,
            filled: filled);
    }

    private static void DrawButtonIcon(
        Vector2 min,
        Vector2 max,
        string icon,
        float logicalSize,
        float opacity,
        Vector4 background,
        bool flipX,
        float strokeWidth,
        bool filled = false)
    {
        var (iconMin, iconMax) = CenteredIconBounds(
            min, max, logicalSize);
        IconInComposited(
            iconMin, iconMax, icon,
            opacity: opacity,
            background: background,
            flipX: flipX,
            strokeWidth: strokeWidth,
            filled: filled);
    }

    private static (Vector2 Min, Vector2 Max) CenteredIconBounds(
        Vector2 min,
        Vector2 max,
        float logicalSize)
    {
        float side = logicalSize * ImGuiHelpers.GlobalScale;
        var iconMin = (min + max - new Vector2(side)) * 0.5f;
        return (iconMin, iconMin + new Vector2(side));
    }

    // ---- On/off icon -----------------------------------------------

    internal static bool RenderTemporaryIconToggle(
        string id,
        Vector2 logicalSize,
        bool selected,
        bool slashed,
        bool disabled,
        string? help,
        Action<Vector2, Vector2, float> content,
        Action? onClick,
        bool dimmed = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        disabled |= Interactive.GreyedOut;
        var hit = Interactive.Reserve(id, logicalSize * scale, disabled);
        float opacity = ToggleOpacity(disabled);
        // A dimmed toggle shows "not the active one" with a fainter icon
        // instead of a slash. It still works as normal, and hovering shows
        // it at full strength.
        if (dimmed && !disabled && !hit.Hovered)
            opacity *= 0.45f;
        var draw = ImGui.GetWindowDrawList();
        PaintTemporaryToggleBox(
            draw, hit.ScreenMin, hit.ScreenMax, selected, hit.Hovered, disabled);
        content(hit.ScreenMin, hit.ScreenMax, opacity);
        if (slashed)
            PaintTemporaryToggleSlash(draw, hit.ScreenMin, hit.ScreenMax);

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Clicked)
            onClick?.Invoke();
        return hit.Clicked;
    }

    /// <summary>
    /// Draws the toggle's box: the fill when selected, the hover shade and
    /// the disabled fade. The icon is drawn separately, and both fade by the
    /// same <see cref="ToggleOpacity"/>.
    /// </summary>
    private static void PaintTemporaryToggleBox(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool hovered,
        bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var background = selected
            ? ActiveTheme.Chrome.SegmentSelected
            : hovered
                ? ActiveTheme.Chrome.WeakOverlay
                : Vector4.Zero;
        background = background.Fade(ToggleOpacity(disabled));
        draw.AddRectFilled(
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            ActiveTheme.Radii.Control * scale);
    }

    /// <summary>The toggle's slash: a diagonal line drawn over the icon to say
    /// it is off.</summary>
    private static void PaintTemporaryToggleSlash(
        ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = ActiveTheme.Spacing.Two * scale;
        draw.AddLine(
            min + new Vector2(inset),
            max - new Vector2(inset),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(ActiveTheme.TextDim)),
            scale);
    }

    /// <summary>How faded the toggle is. The box and the icon both use this,
    /// so they always match.</summary>
    private static float ToggleOpacity(bool disabled) =>
        disabled ? ActiveTheme.Chrome.ControlDisabledOpacity : 1f;

    internal static void DrawLegacyButtonIcon(
        Vector2 min,
        Vector2 max,
        TablerIcon icon,
        float opacity,
        bool flipX)
    {
        IconIn(
            min, max, icon,
            contentScale: ActiveTheme.Controls.IconContentScale,
            opacity: opacity,
            flipX: flipX);
    }

    internal static void DrawLegacyButtonIcon(
        Vector2 min,
        Vector2 max,
        string icon,
        float opacity,
        bool flipX)
    {
        IconIn(
            min, max, icon,
            contentScale: ActiveTheme.Controls.IconContentScale,
            opacity: opacity,
            flipX: flipX);
    }

    internal static float ButtonHeight(ControlStyle style) =>
        ControlSizing.Height(style.Height, ActiveTheme.Controls.ComfortableHeight);

    internal static Vector2 IconButtonSize(ControlStyle style)
    {
        float height = style.Height.Kind == UiHeightKind.Fixed
            ? style.Height.Value
            : ControlSizing.Height(
                style.Height,
                ActiveTheme.Controls.ShellIconAction);
        float width = ControlSizing.Width(
            style,
            height,
            ImGui.GetContentRegionAvail().X / ImGuiHelpers.GlobalScale);
        return new(width, height);
    }

    private static float ButtonPadding(ControlStyle style) =>
        ControlSizing.IsWorkspace(style.Height)
            ? ActiveTheme.Spacing.Six
            : ActiveTheme.Spacing.Eight;
}
