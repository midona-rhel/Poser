using System;
using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Poser.UI;

/// <summary>
/// A sheet icon id resolved to an ImGui texture handle, or 0 when there is
/// none. Two invariants live here and nowhere else:
///
/// <list type="bullet">
/// <item>Failures are REMEMBERED. Sheet icon ids are not guaranteed to exist
/// and the game-icon lookup throws for those, so the resolver takes the
/// try-variant, catches anyway, and never asks a second time — otherwise a
/// catalog with one bad id pays a throw per row per frame.</item>
/// <item>The WRAP is never cached. Shared textures must be re-resolved every
/// frame, so only the handle crosses back to the caller.</item>
/// </list>
///
/// One resolver per surface: the remembered-failure set is the only state,
/// and a surface that outlives its catalog wants it dropped with the surface.
/// </summary>
internal sealed class GameIconResolver
{
    private readonly ITextureProvider _textures;
    private readonly HashSet<uint> _missing = new();

    internal GameIconResolver(ITextureProvider textures) =>
        _textures = textures;

    internal nint Resolve(uint iconId)
    {
        if (iconId == 0 || _missing.Contains(iconId))
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            if (_textures.TryGetFromGameIcon(
                    new GameIconLookup(iconId), out var shared))
                wrap = shared.GetWrapOrDefault();
            else
                _missing.Add(iconId);
        }
        catch (Exception)
        {
            _missing.Add(iconId);
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }
}
