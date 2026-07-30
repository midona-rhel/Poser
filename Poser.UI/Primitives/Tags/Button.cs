using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
            (min, max, opacity) => DrawButtonIcon(
                min, max, icon, iconSize, opacity, flipX, strokeWidth),
            onClick);
    }

    public static bool IconButton(
        string icon,
        Action? onClick = null,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null,
        string? id = null,
        float iconSize = 16f,
        float strokeWidth = 1.5f)
    {
        var size = IconButtonSize(style);
        return RenderIconButton(
            id ?? icon,
            size,
            disabled,
            help,
            (min, max, opacity) => DrawButtonIcon(
                min, max, icon, iconSize, opacity, strokeWidth),
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
        bool slashed = false)
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
                min, max, icon, opacity),
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

        var draw = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control * scale;
        float borderPx = 1f * scale;
        float inset = 0.5f * scale;
        // The hover state advances EVERY frame — a disabled frame drives
        // it toward idle, so disabling while hovered and re-enabling away
        // from the pointer can never replay stale hover fill.
        float eased = AdvanceHover(identity, hit.Hovered && !disabled);
        if (disabled)
        {
            // .btn:disabled is CSS GROUP opacity — the element flattens
            // before 0.35 applies once. ONE path reproduces it through
            // the existing renderers: the chrome draws non-overlapping
            // (fill inset to the border's inner edge, the ring carrying
            // the analytically flattened border-over-fill color — exact
            // for every backdrop), and the label draws through the
            // canonical TextAt path with COMPENSATED color and alpha so
            // that blending the glyphs over the faded fill lands on the
            // group result. For translucent fills the compensation is
            // exact for every backdrop and every glyph coverage; for an
            // opaque fill (Primary) it is exact over the theme surface
            // and bounded by 0.2275·|surface − backdrop| elsewhere,
            // because affine over-blending cannot express a group over
            // an unknown backdrop.
            var ring = FlattenOver(borderIdle, fill);
            ring.W *= opacity;
            var fillFaded = fill;
            fillFaded.W *= opacity;
            draw.AddRectFilled(
                hit.ScreenMin + new Vector2(borderPx),
                hit.ScreenMax - new Vector2(borderPx),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fillFaded)),
                MathF.Max(0f, radius - borderPx));
            draw.AddRect(
                hit.ScreenMin + new Vector2(inset),
                hit.ScreenMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ring)),
                MathF.Max(0f, radius - inset),
                ImDrawFlags.None,
                borderPx);
            var compensated = DisabledLabelCompensation(
                text, fill, theme.Surface, opacity);
            DrawButtonLabelClipped(
                draw, hit.ScreenMin, hit.ScreenMax, label, style, compensated);
        }
        else
        {
            // Enabled: the border blends over the fill exactly as the
            // CSS element composites against the page; the background
            // follows the 150ms hover transition with PREMULTIPLIED
            // color interpolation, as Chromium interpolates rgba.
            var background = PremultipliedLerp(fill, fillHover, eased);
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

        if (!disabled)
        {
            // .btn:focus-visible — 2px primary-60 outline offset 1px,
            // shown for keyboard focus only; pointer interaction never
            // invents one. Disabled buttons draw their label inside the
            // group surface above and can neither focus nor hover.
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

            DrawButtonLabelClipped(
                draw, hit.ScreenMin, hit.ScreenMax, label, style, text);
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

    /// <summary>Centered label through the canonical text path, clipped
    /// to the button's visual bounds.</summary>
    private static void DrawButtonLabelClipped(
        ImDrawListPtr draw, Vector2 min, Vector2 max,
        string label, ControlStyle style, Vector4 color)
    {
        var labelStyle = ButtonLabelStyle(style, color);
        var measured = MeasureText(label, labelStyle);
        var position = min + (max - min - measured) * 0.5f;
        draw.PushClipRect(min, max, true);
        try
        {
            TextAt(position, label, labelStyle);
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    /// <summary>Top layer composited over the bottom layer (source-over),
    /// returned straight-alpha — the flattened color a CSS element shows
    /// where the two overlap before any group opacity applies.</summary>
    private static Vector4 FlattenOver(Vector4 top, Vector4 bottom)
    {
        float alpha = top.W + bottom.W * (1f - top.W);
        if (alpha <= 0f)
            return default;
        var rgb = (new Vector3(top.X, top.Y, top.Z) * top.W
            + new Vector3(bottom.X, bottom.Y, bottom.Z)
                * bottom.W * (1f - top.W)) / alpha;
        return new Vector4(rgb, alpha);
    }

    /// <summary>
    /// Compensated label color/alpha for the disabled group: drawing
    /// glyphs at coverage c over the ALREADY-faded fill must equal the
    /// CSS flatten-then-fade result. For fill alpha < 1 the solution is
    /// exact for every backdrop: alpha = o(1−af)/(1−o·af) and the color
    /// absorbs the excess fill contribution. An opaque fill admits no
    /// backdrop-independent solution, so it references the theme
    /// surface (the capture backdrop) instead.
    /// </summary>
    private static Vector4 DisabledLabelCompensation(
        Vector4 text, Vector4 fill, Vector4 surface, float groupOpacity)
    {
        float af = fill.W;
        if (af < 0.999f)
        {
            float alpha = groupOpacity * (1f - af) / (1f - groupOpacity * af);
            var rgb = (new Vector3(text.X, text.Y, text.Z) * groupOpacity
                - new Vector3(fill.X, fill.Y, fill.Z)
                    * (groupOpacity * af * (1f - alpha))) / alpha;
            return new Vector4(
                Math.Clamp(rgb.X, 0f, 1f),
                Math.Clamp(rgb.Y, 0f, 1f),
                Math.Clamp(rgb.Z, 0f, 1f),
                alpha * text.W);
        }
        var opaque = new Vector3(text.X, text.Y, text.Z)
            - (new Vector3(fill.X, fill.Y, fill.Z)
                - new Vector3(surface.X, surface.Y, surface.Z))
                * (1f - groupOpacity);
        return new Vector4(
            Math.Clamp(opaque.X, 0f, 1f),
            Math.Clamp(opaque.Y, 0f, 1f),
            Math.Clamp(opaque.Z, 0f, 1f),
            groupOpacity * text.W);
    }

    /// <summary>Premultiplied-alpha interpolation — how Chromium
    /// transitions between rgba backgrounds of different alpha.</summary>
    private static Vector4 PremultipliedLerp(Vector4 from, Vector4 to, float t)
    {
        float alpha = from.W + (to.W - from.W) * t;
        if (alpha <= 0f)
            return default;
        var rgb = (new Vector3(from.X, from.Y, from.Z) * from.W * (1f - t)
            + new Vector3(to.X, to.Y, to.Z) * to.W * t) / alpha;
        return new Vector4(rgb, alpha);
    }

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

    // ---- Picto momentary icon button --------------------------------

    // iconButton.module.css: both animatable properties use the Picto
    // --ease-default timing function for 150ms.
    private static readonly Transition IconButtonTransition =
        Transition.CubicBezier(0.15f, 0.4f, 0f, 0.22f, 1f);

    private sealed class IconButtonVisualState
    {
        public Vector4 Background;
        public Vector4 FromBackground;
        public Vector4 TargetBackground;
        public float Opacity;
        public float FromOpacity;
        public float TargetOpacity;
        public float Elapsed;
        public int LastFrame;
    }

    private static readonly Dictionary<uint, IconButtonVisualState>
        IconButtonVisualStates = new();

    private static bool RenderIconButton(
        string id,
        Vector2 logicalSize,
        bool disabled,
        string? help,
        Action<Vector2, Vector2, float> content,
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
        float targetOpacity = hit.Hovered || hit.Active ? 1f : 0.8f;
        var (background, opacity) = AdvanceIconButtonVisual(
            identity,
            disabled ? Vector4.Zero : targetBackground,
            disabled ? 0.8f : targetOpacity);
        if (disabled)
        {
            background = Vector4.Zero;
            opacity = 0.2f;
        }

        var draw = ImGui.GetWindowDrawList();
        // Shared .iconBtn is exactly 5px, independent of the 6px radius
        // used by Picto's bordered actionButton family.
        float radius = 5f * scale;
        var fadedBackground = background;
        fadedBackground.W *= opacity;
        draw.PushClipRect(hit.ScreenMin, hit.ScreenMax, true);
        try
        {
            draw.AddRectFilled(
                hit.ScreenMin,
                hit.ScreenMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(fadedBackground)),
                radius);

            content(hit.ScreenMin, hit.ScreenMax, opacity);
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

    private static (Vector4 Background, float Opacity)
        AdvanceIconButtonVisual(
            uint identity,
            Vector4 targetBackground,
            float targetOpacity)
    {
        int frame = ImGui.GetFrameCount();
        if (!IconButtonVisualStates.TryGetValue(identity, out var state)
            || frame <= state.LastFrame)
        {
            if (IconButtonVisualStates.Count > 512)
                PruneIconButtonVisualStates(frame);
            state = new IconButtonVisualState
            {
                Background = targetBackground,
                FromBackground = targetBackground,
                TargetBackground = targetBackground,
                Opacity = targetOpacity,
                FromOpacity = targetOpacity,
                TargetOpacity = targetOpacity,
                Elapsed = IconButtonTransition.DurationSeconds,
                LastFrame = frame,
            };
            IconButtonVisualStates[identity] = state;
            return (state.Background, state.Opacity);
        }

        if (state.TargetBackground != targetBackground
            || state.TargetOpacity != targetOpacity)
        {
            state.FromBackground = state.Background;
            state.TargetBackground = targetBackground;
            state.FromOpacity = state.Opacity;
            state.TargetOpacity = targetOpacity;
            state.Elapsed = 0f;
        }
        else if (state.Elapsed < IconButtonTransition.DurationSeconds)
        {
            state.Elapsed = MathF.Min(
                IconButtonTransition.DurationSeconds,
                state.Elapsed + ImGui.GetIO().DeltaTime);
            float linear = IconButtonTransition.DurationSeconds > 0f
                ? state.Elapsed / IconButtonTransition.DurationSeconds
                : 1f;
            float eased = IconButtonTransition.Evaluate(linear);
            state.Background = PremultipliedLerp(
                state.FromBackground, state.TargetBackground, eased);
            state.Opacity = state.FromOpacity
                + (state.TargetOpacity - state.FromOpacity) * eased;
        }
        state.LastFrame = frame;
        return (state.Background, state.Opacity);
    }

    private static void PruneIconButtonVisualStates(int frame)
    {
        var stale = new List<uint>();
        foreach (var (key, value) in IconButtonVisualStates)
            if (frame - value.LastFrame > 2)
                stale.Add(key);
        foreach (var key in stale)
            IconButtonVisualStates.Remove(key);
    }

    private static void DrawButtonIcon(
        Vector2 min,
        Vector2 max,
        TablerIcon icon,
        float logicalSize,
        float opacity,
        bool flipX,
        float strokeWidth)
    {
        var (iconMin, iconMax) = CenteredIconBounds(
            min, max, logicalSize);
        IconInComposited(
            iconMin, iconMax, icon,
            opacity: opacity,
            flipX: flipX,
            strokeWidth: strokeWidth);
    }

    private static void DrawButtonIcon(
        Vector2 min,
        Vector2 max,
        string icon,
        float logicalSize,
        float opacity,
        float strokeWidth)
    {
        var (iconMin, iconMax) = CenteredIconBounds(
            min, max, logicalSize);
        IconInComposited(
            iconMin, iconMax, icon,
            opacity: opacity,
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
        float opacity = disabled
            ? ActiveTheme.Chrome.ControlDisabledOpacity
            : 1f;
        var background = selected
            ? ActiveTheme.Chrome.SegmentSelected
            : hit.Hovered
                ? ActiveTheme.Chrome.WeakOverlay
                : Vector4.Zero;
        background.W *= opacity;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            hit.ScreenMin,
            hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            ActiveTheme.Radii.Control * scale);
        content(hit.ScreenMin, hit.ScreenMax, opacity);
        if (slashed)
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
        float opacity)
    {
        IconIn(
            min, max, icon,
            contentScale: ActiveTheme.Controls.IconContentScale,
            opacity: opacity);
    }

    private static float ButtonHeight(ControlStyle style) =>
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
