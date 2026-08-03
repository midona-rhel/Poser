using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

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

public static partial class LegacyCrystarium
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
        TablerIcon icon,
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
            id ?? Tabler.NameFor(icon),
            size,
            disabled,
            help,
            (min, max, opacity, background) => DrawButtonIcon(
                min, max, icon, iconSize, opacity, background, flipX,
                strokeWidth),
            onClick);
    }

    /// <summary>The registry-NAME form, for the glyphs the enum does not carry
    /// ("chevron-down", "x"). Every facet of the enum form is reachable here,
    /// mirroring included: a name is a different way to say WHICH glyph, not a
    /// smaller button.</summary>
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
            disabled,
            help,
            (min, max, opacity, background) => DrawButtonIcon(
                min, max, icon, iconSize, opacity, background, flipX,
                strokeWidth),
            onClick);
    }

    /// <summary>
    /// Slice-5 bridge for controls whose selected/slashed state persists.
    /// Momentary actions use <see cref="IconButton(TablerIcon, Action?,
    /// ControlStyle, bool, string?, string?, bool, float, float)"/>.
    /// </summary>
    public static bool TemporaryIconToggle(
        TablerIcon icon,
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
            id ?? Tabler.NameFor(icon),
            size,
            selected,
            slashed,
            disabled,
            help,
            (min, max, opacity) => DrawLegacyButtonIcon(
                min, max, icon, opacity, flipX),
            onClick);
    }

    /// <summary>Slice-5 bridge for registered custom SVG toggles.</summary>
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

    /// <summary>CSS border-box intrinsic width: measured label + the
    /// canonical horizontal padding per side + the 1px border per side.</summary>
    internal static float IntrinsicButtonWidth(
        string label, ControlStyle style) =>
        MeasureText(label, ButtonLabelStyle(style)).X / ImGuiHelpers.GlobalScale
            + ButtonPadding(style) * 2f
            + 2f;

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
    internal static readonly Vector4 DangerText =
        new(1f, 154f / 255f, 164f / 255f, 1f);            // #ff9aa4
    internal static readonly Vector4 DangerBorder =
        new(1f, 71f / 255f, 87f / 255f, 0.35f);           // rgba(255,71,87,.35)
    internal static readonly Vector4 DangerFill =
        new(1f, 71f / 255f, 87f / 255f, 0.08f);           // rgba(255,71,87,.08)
    internal static readonly Vector4 DangerFillHover =
        new(1f, 71f / 255f, 87f / 255f, 0.15f);           // rgba(255,71,87,.15)

    /// <summary>.btn's <c>transition: background 150ms ease</c> — CSS
    /// `ease` is cubic-bezier(0.25, 0.1, 0.25, 1). Background only; the
    /// border and text switch instantly, exactly like the CSS.</summary>
    internal static readonly Transition BackgroundTransition =
        Transition.CubicBezier(0.15f, 0.25f, 0.1f, 0.25f, 1f);

    // Motion channels this component owns. Both stores key by stable
    // ImGui identity (the same seed InvisibleButton hashes); the two
    // icon-button channels share one elapsed clock under that identity,
    // while the text button's hover ramp is the identity's single
    // constant-rate entry and needs no channel of its own.
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
        var hit = Interactive.Reserve(id, size, disabled);
        PaintTextButton(hit, identity, label, style, variant, disabled);

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
            onClick?.Invoke();
        return hit.Activated;
    }

    /// <summary>
    /// The text button's complete paint. Everything visual lives here; the
    /// caller owns only reservation, help, and activation.
    /// </summary>
    internal static void PaintTextButton(
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
    /// The button BOX alone — background, border, and the disabled group —
    /// returning the color its label must take. Split out so the retained
    /// declarative tree can drive the SAME pixels while composing the label
    /// as a real child element instead of a painter argument.
    /// </summary>
    internal static Vector4 PaintTextButtonBox(
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
        // The hover state advances EVERY frame — a disabled frame drives
        // it toward idle, so disabling while hovered and re-enabling away
        // from the pointer can never replay stale hover fill.
        float eased = BackgroundTransition.Evaluate(
            Motion.Progress(
                identity,
                hit.Hovered && !disabled,
                BackgroundTransition.DurationSeconds));
        if (disabled)
        {
            // .btn:disabled is CSS GROUP opacity — the element flattens
            // before 0.35 applies once. This is THE canonical recipe;
            // ControlPaint.DisabledGroup owns it now, and the label draws
            // through the canonical TextAt path with the compensated
            // color the recipe hands back. For translucent fills that
            // compensation is exact for every backdrop and every glyph
            // coverage; for an opaque fill (Primary) it is exact over the
            // theme surface and bounded by 0.2275·|surface − backdrop|
            // elsewhere, because affine over-blending cannot express a
            // group over an unknown backdrop.
            var content = ControlPaint.DisabledGroup(
                draw, hit.ScreenMin, hit.ScreenMax,
                radius, borderPx, fill, borderIdle, opacity);
            return content.Label(text);
        }
        else
        {
            // Enabled: the border blends over the fill exactly as the
            // CSS element composites against the page; the background
            // follows the 150ms hover transition with PREMULTIPLIED
            // color interpolation, as Chromium interpolates rgba.
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

        // NO focus-visible outline — PRODUCT DECISION (user): this is a
        // native-styled UI, not a web page; Picto's .btn:focus-visible ring
        // is deliberately not reproduced. Keyboard activation (Enter/Space
        // via Interactive.Reserve) still works; there is simply no web-style
        // focus chrome anywhere in Crystarium.
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

    /// <summary>Centered label through the canonical text path, clipped
    /// to the button's visual bounds.</summary>
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

    // ---- Picto momentary icon button --------------------------------

    // iconButton.module.css: both animatable properties use the Picto
    // --ease-default timing function for 150ms.
    private static readonly Transition IconButtonTransition =
        Transition.CubicBezier(0.15f, 0.4f, 0f, 0.22f, 1f);

    /// <summary>.iconBtn:disabled — a CSS literal of the shared icon button,
    /// not a theme token, and the ONE home for it: the retained twin's sheet
    /// states its disabled look from here.</summary>
    internal const float IconButtonDisabledOpacity = 0.2f;

    private static bool RenderIconButton(
        string id,
        Vector2 logicalSize,
        bool disabled,
        string? help,
        Action<Vector2, Vector2, float, Vector4> content,
        Action? onClick)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = logicalSize * scale;
        uint identity = ImGui.GetID(id);
        var hit = Interactive.Reserve(
            id, size, disabled, activateOnSpace: true);
        var theme = ActiveTheme;
        var targetBackground = hit.Active
            ? theme.Chrome.ActiveOverlay
            : hit.Hovered
                ? theme.Chrome.WeakOverlay
                : Vector4.Zero;
        // Picto's :active rule changes only the background. Opacity is
        // controlled exclusively by :hover, so dragging a held button
        // outside returns the complete element group to its resting .8.
        float targetOpacity = hit.Hovered ? 1f : 0.8f;
        // One group under one identity: the background and the opacity
        // share a clock, so pressing a button that is still fading in
        // restarts both together, exactly like the CSS element does.
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
        // Shared .iconBtn is exactly 5px, independent of the 6px radius
        // used by Picto's bordered actionButton family.
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
        }
        finally
        {
            draw.PopClipRect();
        }

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
            onClick?.Invoke();
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
        float strokeWidth)
    {
        var (iconMin, iconMax) = CenteredIconBounds(
            min, max, logicalSize);
        IconInComposited(
            iconMin, iconMax, icon,
            opacity: opacity,
            background: background,
            flipX: flipX,
            strokeWidth: strokeWidth);
    }

    private static void DrawButtonIcon(
        Vector2 min,
        Vector2 max,
        string icon,
        float logicalSize,
        float opacity,
        Vector4 background,
        bool flipX,
        float strokeWidth)
    {
        var (iconMin, iconMax) = CenteredIconBounds(
            min, max, logicalSize);
        IconInComposited(
            iconMin, iconMax, icon,
            opacity: opacity,
            background: background,
            flipX: flipX,
            strokeWidth: strokeWidth);
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

    // ---- Temporary persistent icon-toggle bridge (slice 5) ----------

    private static bool RenderTemporaryIconToggle(
        string id,
        Vector2 logicalSize,
        bool selected,
        bool slashed,
        bool disabled,
        string? help,
        Action<Vector2, Vector2, float> content,
        Action? onClick)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var hit = Interactive.Reserve(id, logicalSize * scale, disabled);
        float opacity = ToggleOpacity(disabled);
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
    /// The persistent toggle's BOX: the selection fill, the hover overlay and
    /// the disabled fade, at the control radius. Split out for the same reason
    /// the text button's box was — the retained twin drives these exact pixels
    /// while composing its glyph as a real child element.
    /// </summary>
    internal static void PaintTemporaryToggleBox(
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

    /// <summary>The toggle's SLASH — the inset diagonal that says "this state
    /// is off", drawn OVER the glyph it crosses.</summary>
    internal static void PaintTemporaryToggleSlash(
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

    /// <summary>The toggle's group fade: ONE constant, so the box, the glyph
    /// and the retained twin's content can never disagree about it.</summary>
    internal static float ToggleOpacity(bool disabled) =>
        disabled ? ActiveTheme.Chrome.ControlDisabledOpacity : 1f;

    private static void DrawLegacyButtonIcon(
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

    private static void DrawLegacyButtonIcon(
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

    private static Vector2 IconButtonSize(ControlStyle style)
    {
        float height = style.Height.Kind == UiHeightKind.Fixed
            ? style.Height.Value
            : ControlSizing.Height(
                style.Height,
                ActiveTheme.Controls.ShellIconAction);
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
