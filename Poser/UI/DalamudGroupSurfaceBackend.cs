using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.UI;

namespace Poser.UI.Host;

/// <summary>In-game <see cref="IGroupSurfaceBackend"/>: straight-alpha
/// RGBA8 surfaces become Dalamud textures, so group-composited chrome
/// (disabled buttons) renders identically in game and in the
/// conformance capture host.</summary>
internal sealed class DalamudGroupSurfaceBackend(ITextureProvider textures)
    : IGroupSurfaceBackend
{
    private readonly Dictionary<nint, IDalamudTextureWrap> _wraps = new();

    public nint CreateTexture(byte[] rgba, int width, int height)
    {
        var wrap = textures.CreateFromRaw(
            RawImageSpecification.Rgba32(width, height), rgba);
        nint handle = (nint)wrap.Handle.Handle;
        _wraps[handle] = wrap;
        return handle;
    }

    public void DestroyTexture(nint texture)
    {
        if (_wraps.Remove(texture, out var wrap))
            wrap.Dispose();
    }
}
