using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Opaque texture handle + dimensions, owned by the host plugin. Norvrandt does
/// not load textures itself — the host wraps its texture system (Dalamud's
/// <c>IDalamudTextureWrap</c>, raw GL/D3D textures, etc.) into this interface.
///
/// <para>Implementations may load asynchronously; check <see cref="IsLoaded"/>
/// before reading <see cref="TextureHandle"/>. Norvrandt skips drawing when
/// <see cref="IsLoaded"/> is false.</para>
/// </summary>
public interface IImageSource
{
    /// <summary>ImGui texture id, ready to feed into <c>ImDrawList.AddImage</c>.</summary>
    ImTextureID TextureHandle { get; }

    /// <summary>Texture dimensions in pixels.</summary>
    Vector2 Size { get; }

    /// <summary>True when the texture is ready to render. False during async load / failure.</summary>
    bool IsLoaded { get; }
}
