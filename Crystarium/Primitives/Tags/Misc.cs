using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Thin separator line at 50% border opacity.</summary>
    public static void Separator()
    {
        Controls.PoserUI.Separator();
    }

    /// <summary>Image element. Renders a Dalamud texture wrap at the given size.</summary>
    public static void Image(IDalamudTextureWrap texture, Vector2 size, Vector4? tint = null)
    {
        if (texture == null) return;
        if (tint.HasValue)
            ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, tint.Value);
        else
            ImGui.Image(texture.Handle, size);
    }
}
