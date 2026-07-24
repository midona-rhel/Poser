using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Textures.TextureWraps;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Plain inline text. Cascade-inherited color/font/opacity unless overridden.</summary>
    public static void Text(string text)
        => TextCore(text, default, null);
    public static void Text(string text, StyleClass cls)
        => TextCore(text, cls, null);
    public static void Text(string text, StyleClassSet classes)
        => TextCore(text, classes, null);
    public static void Text(string text, in TextProps props)
        => TextCore(text, props.Classes, props.Style);

    private static void TextCore(string text, StyleClassSet classes, TextStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var resolved = Stylesheet.ResolveText(classes, PseudoState.None);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);
        if (resolved.Display == UI.Display.None) return;

        // Project TextStyle → ElementStyle slice for the renderer.
        var textElemStyle = new ElementStyle
        {
            Color          = resolved.Color,
            TextAlign      = resolved.TextAlign,
            TextOverflow   = resolved.TextOverflow,
            WhiteSpace     = resolved.WhiteSpace,
            LineHeight     = resolved.LineHeight,
            LetterSpacing  = resolved.LetterSpacing,
            TextShadow     = resolved.TextShadow,
        };

        // Row-aware path: when called inside a flex row's children lambda, register as a
        // row item so we participate in flex layout instead of falling through ImGui's
        // vertical auto-flow (which would push the next element to a new line).
        if (Norvrandt.IsInRow)
        {
            // Width: honor explicit Width if Fixed, otherwise Auto (intrinsic measure).
            Sizing width = (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
                ? resolved.Width.Value
                : Sizing.Auto;
            Norvrandt.RegisterRowItem(width, null, null, (cellW, cellH) =>
                DrawTextInCell(text, textElemStyle, cellW, cellH));
            return;
        }

        // Standalone path: lay out at the current ImGui cursor.
        float scale = ImGuiHelpers.GlobalScale;

        if (resolved.Margin.HasValue && resolved.Margin.Value.Top > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + resolved.Margin.Value.Top * scale);

        float maxPx;
        if (resolved.MaxWidth.HasValue && resolved.MaxWidth.Value.Mode == SizingMode.Fixed)
            maxPx = resolved.MaxWidth.Value.Value * scale;
        else if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            maxPx = resolved.Width.Value.Value * scale;
        else
            maxPx = Norvrandt.AvailableWidth;

        var size = TextRenderer.Measure(text, textElemStyle, maxPx);

        float ambientH = Norvrandt.AvailableHeight;
        if (ambientH > size.Y)
        {
            float offsetY = (ambientH - size.Y) / 2f;
            if (offsetY > 0f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        }

        var origin = ImGui.GetCursorScreenPos();
        var boxMax = origin + new Vector2(maxPx, size.Y);

        var defaultColor = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Norvrandt.Sheet.CurrentTheme.Text));
        TextRenderer.Draw(ImGui.GetWindowDrawList(), origin, boxMax, text, textElemStyle, defaultColor);

        ImGui.Dummy(new Vector2(maxPx, size.Y));

        if (resolved.Margin.HasValue && resolved.Margin.Value.Bottom > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + resolved.Margin.Value.Bottom * scale);
    }

    /// <summary>Draw text inside a row cell of explicit (width, height) — vertically centered.</summary>
    private static void DrawTextInCell(string text, in ElementStyle style, float cellW, float cellH)
    {
        var size = TextRenderer.Measure(text, style, cellW);
        var origin = ImGui.GetCursorScreenPos();
        // Vertical centering inside the cell.
        if (cellH > size.Y) origin.Y += (cellH - size.Y) * 0.5f;
        var boxMax = origin + new Vector2(cellW, size.Y);
        var defaultColor = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Norvrandt.Sheet.CurrentTheme.Text));
        TextRenderer.Draw(ImGui.GetWindowDrawList(), origin, boxMax, text, style, defaultColor);
    }

    /// <summary>Thin separator line at 50% border opacity.</summary>
    /// <summary>Image element. Renders an <see cref="IImageSource"/> at the given size.</summary>
    public static void Image(IImageSource source, Vector2 size, Vector4? tint = null)
    {
        if (source is null || !source.IsLoaded) return;
        if (tint.HasValue)
            ImGui.Image(source.TextureHandle, size, Vector2.Zero, Vector2.One, tint.Value);
        else
            ImGui.Image(source.TextureHandle, size);
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
