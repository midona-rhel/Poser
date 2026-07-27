using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Plain inline body text from the active theme.</summary>
    public static void Text(string text)
        => Text(
            text,
            ActiveTheme.Typography.BodySize,
            FontWeight.Regular,
            ActiveTheme.Text);

    public static void Text(string text, float size, FontWeight weight,
        Vector4 color, bool mono = false, bool wrap = false)
    {
        var font = FontRegistry.Resolve(
            mono ? FontFamily.Mono : FontFamily.Default,
            weight,
            size);
        bool fontPushed = font is { Available: true };
        if (fontPushed)
            font!.Push();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        if (wrap)
        {
            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.TextUnformatted(text);
        }
        ImGui.PopStyleColor();
        if (fontPushed)
            font!.Pop();
    }

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
