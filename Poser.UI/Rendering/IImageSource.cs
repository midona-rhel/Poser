using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Opaque texture handle and dimensions, owned by the host plugin. Widgets do
/// not load textures themselves; the host wraps its texture system (Dalamud's
/// <c>IDalamudTextureWrap</c>, raw GL/D3D textures, etc.) into this interface.
///
/// <para>Implementations may load asynchronously; check <see cref="IsLoaded"/>
/// before reading <see cref="TextureHandle"/>. The widgets skip drawing when
/// <see cref="IsLoaded"/> is false.</para>
/// </summary>
public interface IImageSource
{
    /// <summary>ImGui texture id, ready to feed into <c>ImDrawList.AddImage</c>.</summary>
    ImTextureID TextureHandle { get; }

    /// <summary>Texture dimensions in pixels.</summary>
    Vector2 Size { get; }

    /// <summary>True when the texture is ready to render; false while loading or after failure.</summary>
    bool IsLoaded { get; }
}
