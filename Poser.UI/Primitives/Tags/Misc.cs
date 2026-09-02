using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Image element. Renders an <see cref="IImageSource"/> at the given size.</summary>
    public static void Image(IImageSource source, Vector2 size, Vector4? tint = null)
    {
        if (source is null || !source.IsLoaded) return;
        if (tint.HasValue)
            ImGui.Image(source.TextureHandle, size, Vector2.Zero, Vector2.One, tint.Value);
        else
            ImGui.Image(source.TextureHandle, size);
    }

    /// <summary>A square picture that opens something — an item's icon on
    /// its card. A well-coloured box holds the image inset; without an
    /// image the fallback glyph stands in. Two rows tall by convention.</summary>
    public static bool ImageTile(
        string id, nint texture, float side, Action? onClick = null,
        TablerIcon fallback = TablerIcon.Photo, string? help = null,
        bool disabled = false, bool selected = false)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var hit = Interactive.Reserve(id, new Vector2(side * scale), disabled);
        var draw = ImGui.GetWindowDrawList();
        var fill = !disabled && (hit.Hovered || hit.Active)
            ? theme.Chrome.WeakOverlay
            : selected ? theme.Chrome.ActiveOverlay : theme.Chrome.InputWell;
        BoxRenderer.Draw(draw, hit.ScreenMin, hit.ScreenMax, new BoxStyle
        {
            BackgroundColor = disabled ? fill.Fade(theme.Chrome.DisabledOpacity) : fill,
            BorderRadius = theme.Radii.Control,
            BorderWidth = selected ? 1f : 0f,
            BorderTopColor = selected ? theme.Chrome.Primary : null,
            BorderRightColor = selected ? theme.Chrome.Primary : null,
            BorderBottomColor = selected ? theme.Chrome.Primary : null,
            BorderLeftColor = selected ? theme.Chrome.Primary : null,
        });
        float inset = theme.Spacing.Two * scale;
        if (texture != 0)
            draw.AddImage(
                new ImTextureID(texture),
                hit.ScreenMin + new Vector2(inset),
                hit.ScreenMax - new Vector2(inset),
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    disabled ? Vector4.One.Fade(theme.Chrome.DisabledOpacity) : Vector4.One)));
        else
        {
            var center = (hit.ScreenMin + hit.ScreenMax) * 0.5f;
            var glyph = new Vector2(theme.Controls.IconSize * 0.5f * scale);
            IconIn(center - glyph, center + glyph, fallback, theme.TextDim, disabled: disabled);
        }
        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
            onClick?.Invoke();
        return hit.Activated;
    }

    /// <summary>A box of one colour that opens something — a dye's row,
    /// the colour being the value. A label reads in contrast on it. No
    /// colour paints the empty well.</summary>
    public static bool ColorTile(
        string id, Vector4? color, float width, float height,
        Action? onClick = null, string? label = null, string? help = null,
        bool disabled = false)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var hit = Interactive.Reserve(id, new Vector2(width * scale, height * scale), disabled);
        var draw = ImGui.GetWindowDrawList();
        var fill = color is { } paint ? paint with { W = 1f } : theme.Chrome.InputWell;
        BoxRenderer.Draw(draw, hit.ScreenMin, hit.ScreenMax, new BoxStyle
        {
            BackgroundColor = disabled ? fill.Fade(theme.Chrome.DisabledOpacity) : fill,
            BorderRadius = theme.Radii.Control,
        });
        if (!disabled && (hit.Hovered || hit.Active))
            BoxRenderer.Draw(draw, hit.ScreenMin, hit.ScreenMax, new BoxStyle
            {
                BackgroundColor = theme.Chrome.WeakOverlay,
                BorderRadius = theme.Radii.Control,
            });
        if (!string.IsNullOrEmpty(label))
        {
            var style = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Color = color is { } painted ? painted.ContrastText() : theme.Text,
                Disabled = disabled,
            };
            float inset = theme.Spacing.Six * scale;
            var bandMin = hit.ScreenMin + new Vector2(inset, 0f);
            var bandSize = hit.ScreenMax - hit.ScreenMin - new Vector2(inset * 2f, 0f);
            TextInBand(bandMin, bandSize, TruncateText(label!, style, bandSize.X), style);
        }
        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        if (hit.Activated)
            onClick?.Invoke();
        return hit.Activated;
    }

    /// <summary>Direct overload for Dalamud textures (back-compat with existing call sites).</summary>
    public static void Image(IDalamudTextureWrap texture, Vector2 size, Vector4? tint = null)
    {
        if (texture == null) return;
        if (tint.HasValue)
            ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, tint.Value);
        else
            ImGui.Image(texture.Handle, size);
    }
}
