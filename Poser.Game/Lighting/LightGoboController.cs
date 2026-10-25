using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Poser.Domain.Scene;
using Poser.Game.Services;

namespace Poser.Game.Lighting;

/// <summary>
/// Projected light textures (gobos): the native set-texture/classify pair,
/// the embedded library, and the apply/clear/adopt writes on one light.
/// Availability is tracked apart from the light factory's on purpose: a patch
/// that only breaks the texture pair must cost gobos, not lights.
/// </summary>
internal sealed unsafe class LightGoboController
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    /// <summary>Assigns a resource path as the light's projected texture.</summary>
    private readonly delegate* unmanaged<GameLight*, uint*, byte*, byte> _setLightTexture;

    /// <summary>Classifies a resource path into its resource category, which
    /// the texture assignment takes as its second argument.</summary>
    private readonly delegate* unmanaged<uint*, byte*, uint*> _classifyPath;

    public LightGoboController(ISigScanner sigScanner, IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log = log;
        Gobos = GoboLibrary.Load();

        var textureAddress = LightingService.TryScan(
            sigScanner,
            log,
            "light set-texture",
            Poser.Game.Natives.NativeSignatures.Light_LoadProjectedTexture);
        var classifyAddress = LightingService.TryScan(
            sigScanner,
            log,
            "resource-path classify",
            Poser.Game.Natives.NativeSignatures.ResourceCategory_FromPath);
        if (textureAddress is { } texture && classifyAddress is { } classify)
        {
            _setLightTexture =
                (delegate* unmanaged<GameLight*, uint*, byte*, byte>)texture;
            _classifyPath = (delegate* unmanaged<uint*, byte*, uint*>)classify;
            AreGobosAvailable = Gobos.Count > 0;
        }
    }

    public IReadOnlyList<GoboEntry> Gobos { get; }

    /// <summary>False when either gobo signature failed or the embedded
    /// library is empty; the light service itself stays usable.</summary>
    public bool AreGobosAvailable { get; }

    public bool ApplyGobo(Light typed, GoboEntry gobo)
    {
        if (!AreGobosAvailable)
            return false;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Warning("LightingService: gobos must be applied on the framework thread");
            return false;
        }
        if (!SupportsGobo(typed.Kind))
            return false;

        return ApplyGoboPath(typed, gobo.Path);
    }

    /// <summary>Only spot and area lights project a texture; the game ignores
    /// it on the other two kinds.</summary>
    public static bool SupportsGobo(LightKind kind) =>
        kind is LightKind.Spot or LightKind.Area;

    [SkipLocalsInit]
    public bool ApplyGoboPath(Light light, string path)
    {
        if (!AreGobosAvailable || string.IsNullOrEmpty(path))
            return false;

        var native = light.NativePtr;
        if (native == null)
            return false;

        // The native assignment early-returns when a texture handle is already
        // present, so any previous gobo has to be released first.
        ClearGoboNative(light);

        try
        {
            var byteCount = Encoding.UTF8.GetByteCount(path);
            Span<byte> buffer = byteCount > 511
                ? (Span<byte>)new byte[byteCount + 1]
                : stackalloc byte[512];
            Encoding.UTF8.GetBytes(path.AsSpan(), buffer);
            buffer[byteCount] = 0;

            fixed (byte* pathPtr = buffer)
            {
                var category = 0xFFFFFFFFu;
                _classifyPath(&category, pathPtr);
                var result = _setLightTexture(native, &category, pathPtr);

                native->UpdateRender();
                native->Update();

                if (result == 0)
                {
                    _log.Warning(
                        $"LightingService: the game refused gobo '{path}' for light '{light.Name}'");
                    return false;
                }
            }

            light.SetGoboPath(path);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to apply gobo '{path}': {ex.Message}");
            return false;
        }
    }

    public void ClearGoboNative(Light light)
    {
        var native = light.NativePtr;
        if (native == null)
        {
            light.SetGoboPath(null);
            return;
        }

        try
        {
            if (native->ProjectedCubemapTexture != null)
            {
                native->ProjectedCubemapTexture->DecRef();
                native->ProjectedCubemapTexture = null;
            }
            if (native->LightRenderObject != null)
                native->LightRenderObject->Texture = null;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to clear a gobo: {ex.Message}");
        }

        light.SetGoboPath(null);
    }

    /// <summary>Records the original projected texture path for a supported light kind.
    /// The path need not appear in the embedded library.</summary>
    public void AdoptGobo(Light light, GameLight* original)
    {
        if (!AreGobosAvailable || original->ProjectedCubemapTexture == null)
            return;

        string path;
        try
        {
            path = original->ProjectedCubemapTexture->FileName.ToString();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(path) || !SupportsGobo(light.Kind))
            return;
        light.SetGoboPath(path);
    }
}
