using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

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
