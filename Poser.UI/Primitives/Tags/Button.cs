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
            // .btn:disabled is CSS GROUP opacity: fill, border, glyph
            // coverage, and their antialiasing flatten into ONE surface
            // before 0.35 applies once. Sequential primitive fading
            // cannot express that, so the surface is CPU-composed and
            // drawn as a single textured quad. Without a registered
            // backend (or atlas pixels), the nearest sequential
            // approximation below still avoids overlapping draws.
            if (!DrawDisabledGroup(
                    draw, hit.ScreenMin, hit.ScreenMax, label, style,
                    variant, fill, borderIdle, text, radius, borderPx, opacity))
            {
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
                DrawButtonLabelClipped(
                    draw, hit.ScreenMin, hit.ScreenMax, label, style,
                    text with { W = text.W * opacity });
            }
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

    /// <summary>Composes the disabled button as ONE flattened surface —
    /// fill, border, glyph coverage, antialiasing — with the group
    /// opacity applied once, and draws it as a single textured quad.
    /// Returns false when no group-surface backend or atlas pixel data
    /// is available.</summary>
    private static unsafe bool DrawDisabledGroup(
        ImDrawListPtr draw, Vector2 min, Vector2 max,
        string label, ControlStyle style, ButtonVariant variant,
        Vector4 fill, Vector4 border, Vector4 textColor,
        float radiusPx, float borderPx, float groupOpacity)
    {
        if (!GroupSurface.Available)
            return false;
        int width = (int)MathF.Round(max.X - min.X);
        int height = (int)MathF.Round(max.Y - min.Y);
        if (width <= 0 || height <= 0)
            return false;

        var labelStyle = ButtonLabelStyle(style, textColor);
        long key = CombineHash(
            HashCode.Combine(label, variant, width, height),
            HashCode.Combine(fill, border, textColor, groupOpacity));
        var texture = GroupSurface.Acquire(
            key, width, height, ImGui.GetFrameCount(),
            () => ComposeDisabledButton(
                width, height, label, labelStyle, fill, border, textColor,
                radiusPx, borderPx, groupOpacity));
        if (texture is not { } handle)
            return false;
        draw.AddImage(
            new ImTextureID(handle),
            min,
            min + new Vector2(width, height),
            Vector2.Zero,
            Vector2.One,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 1f))));
        return true;
    }

    private static long CombineHash(int high, int low) =>
        ((long)high << 32) | (uint)low;

    /// <summary>CPU flatten of the disabled button in straight alpha:
    /// SDF-antialiased rounded fill and border ring (background paints to
    /// the border box, the border blends over it, exactly the CSS box),
    /// glyph coverage bilinearly sampled from the shared font atlas at
    /// the same centered positions the enabled path draws, then ONE
    /// group-opacity multiply.</summary>
    private static unsafe byte[] ComposeDisabledButton(
        int width, int height, string label, TextStyle labelStyle,
        Vector4 fill, Vector4 border, Vector4 textColor,
        float radiusPx, float borderPx, float groupOpacity)
    {
        var buffer = new Vector4[width * height];

        var half = new Vector2(width, height) * 0.5f;
        float CoverRounded(Vector2 p, Vector2 halfSize, float r)
        {
            var q = Vector2.Abs(p - half) - (halfSize - new Vector2(r));
            float outside = new Vector2(
                MathF.Max(q.X, 0f), MathF.Max(q.Y, 0f)).Length();
            float inside = MathF.Min(MathF.Max(q.X, q.Y), 0f);
            float sdf = outside + inside - r;
            return Math.Clamp(0.5f - sdf, 0f, 1f);
        }

        void Composite(int x, int y, Vector4 color, float coverage)
        {
            float alpha = color.W * coverage;
            if (alpha <= 0f)
                return;
            ref var dst = ref buffer[y * width + x];
            float outAlpha = alpha + dst.W * (1f - alpha);
            if (outAlpha <= 0f)
                return;
            var rgb = (new Vector3(color.X, color.Y, color.Z) * alpha
                + new Vector3(dst.X, dst.Y, dst.Z) * dst.W * (1f - alpha))
                / outAlpha;
            dst = new Vector4(rgb, outAlpha);
        }

        var innerHalf = half - new Vector2(borderPx);
        float innerRadius = MathF.Max(0f, radiusPx - borderPx);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float outer = CoverRounded(p, half, radiusPx);
            float inner = CoverRounded(p, innerHalf, innerRadius);
            Composite(x, y, fill, outer);
            Composite(x, y, border, MathF.Max(0f, outer - inner));
        }

        // Glyph coverage from the shared atlas at the enabled path's
        // exact centered, snapped position and global scale.
        var font = FontRegistry.Resolve(
            labelStyle.Family,
            labelStyle.Weight ?? FontWeight.Regular,
            labelStyle.Size ?? ActiveTheme.Typography.BodySize);
        var locked = font is { Available: true } ? font.TryLock(out _) : null;
        if (locked != null)
        {
            try
            {
                var imFont = locked.ImFont;
                var atlas = ImGui.GetIO().Fonts;
                byte* atlasPixelsRaw = null;
                int atlasW = 0, atlasH = 0;
                atlas.GetTexDataAsAlpha8(
                    0, &atlasPixelsRaw, &atlasW, &atlasH);
                if (atlasPixelsRaw == null || atlasW == 0)
                    return FinishCompose(buffer, width, height, groupOpacity);
                nint atlasPixels = (nint)atlasPixelsRaw;

                float glyphScale = Dalamud.Interface.Utility
                    .ImGuiHelpers.GlobalScale;
                var measured = MeasureText(label, labelStyle);
                var start = ActiveTheme.Optical.Snap(
                    (new Vector2(width, height) - measured) * 0.5f);
                float penX = start.X;
                foreach (char c in label)
                {
                    ref var glyph = ref *imFont.FindGlyph(c);
                    float x0 = penX + glyph.X0 * glyphScale;
                    float y0 = start.Y + glyph.Y0 * glyphScale;
                    float x1 = penX + glyph.X1 * glyphScale;
                    float y1 = start.Y + glyph.Y1 * glyphScale;
                    int px0 = Math.Max(0, (int)MathF.Floor(x0));
                    int py0 = Math.Max(0, (int)MathF.Floor(y0));
                    int px1 = Math.Min(width, (int)MathF.Ceiling(x1));
                    int py1 = Math.Min(height, (int)MathF.Ceiling(y1));
                    for (int py = py0; py < py1; py++)
                    for (int px = px0; px < px1; px++)
                    {
                        float tx = (px + 0.5f - x0) / MathF.Max(x1 - x0, 1e-4f);
                        float ty = (py + 0.5f - y0) / MathF.Max(y1 - y0, 1e-4f);
                        if (tx < 0f || tx >= 1f || ty < 0f || ty >= 1f)
                            continue;
                        float u = (glyph.U0 + (glyph.U1 - glyph.U0) * tx) * atlasW - 0.5f;
                        float v = (glyph.V0 + (glyph.V1 - glyph.V0) * ty) * atlasH - 0.5f;
                        int ui = (int)MathF.Floor(u);
                        int vi = (int)MathF.Floor(v);
                        float fu = u - ui;
                        float fv = v - vi;
                        float coverage =
                            SampleAtlas(atlasPixels, atlasW, atlasH, ui, vi)
                                * (1f - fu) * (1f - fv)
                            + SampleAtlas(atlasPixels, atlasW, atlasH, ui + 1, vi)
                                * fu * (1f - fv)
                            + SampleAtlas(atlasPixels, atlasW, atlasH, ui, vi + 1)
                                * (1f - fu) * fv
                            + SampleAtlas(atlasPixels, atlasW, atlasH, ui + 1, vi + 1)
                                * fu * fv;
                        Composite(px, py, textColor, coverage);
                    }
                    penX += glyph.AdvanceX * glyphScale;
                }
            }
            finally
            {
                locked.Dispose();
            }
        }

        return FinishCompose(buffer, width, height, groupOpacity);
    }

    private static unsafe float SampleAtlas(
        nint pixels, int atlasWidth, int atlasHeight, int x, int y) =>
        x < 0 || y < 0 || x >= atlasWidth || y >= atlasHeight
            ? 0f
            : ((byte*)pixels)[y * atlasWidth + x] / 255f;

    private static byte[] FinishCompose(
        Vector4[] buffer, int width, int height, float groupOpacity)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < buffer.Length; i++)
        {
            var px = buffer[i];
            bytes[i * 4 + 0] = (byte)Math.Clamp((int)MathF.Round(px.X * 255f), 0, 255);
            bytes[i * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(px.Y * 255f), 0, 255);
            bytes[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round(px.Z * 255f), 0, 255);
            bytes[i * 4 + 3] = (byte)Math.Clamp(
                (int)MathF.Round(px.W * groupOpacity * 255f), 0, 255);
        }
        return bytes;
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
