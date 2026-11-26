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
/// Gobos: textures a spot or area light projects. Holds the list of gobos
/// built into Poser and puts a gobo on a light or takes it off. Gobos can be
/// unavailable while lights still work, for example after a game patch moves
/// only the texture functions.
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

    /// <summary>Projects the texture at <paramref name="path"/>. When the game
    /// refuses it, the light's previous gobo is put back and false is returned.</summary>
    public bool ApplyGoboPath(Light light, string path)
    {
        if (!AreGobosAvailable || light.NativePtr == null)
            return false;
        var previous = light.GoboPath;
        if (SetTexture(light, path))
            return true;
        _log.Warning($"LightingService: the game refused gobo '{path}' for light '{light.Name}'");
        if (previous != null)
            SetTexture(light, previous);
        return false;
    }

    [SkipLocalsInit]
    private bool SetTexture(Light light, string path)
    {
        // The game's texture call does nothing while the light already has a
        // texture, so the old one is taken off first.
        ClearGoboNative(light);
        var native = light.NativePtr;

        var byteCount = Encoding.UTF8.GetByteCount(path);
        Span<byte> buffer = byteCount > 511
            ? (Span<byte>)new byte[byteCount + 1]
            : stackalloc byte[512];
        Encoding.UTF8.GetBytes(path.AsSpan(), buffer);
        buffer[byteCount] = 0;

        byte result;
        fixed (byte* pathPtr = buffer)
        {
            var category = 0xFFFFFFFFu;
            _classifyPath(&category, pathPtr);
            result = _setLightTexture(native, &category, pathPtr);
        }
        native->UpdateRender();
        native->Update();
        if (result == 0)
            return false;
        light.SetGoboPath(path);
        return true;
    }

    public void ClearGoboNative(Light light)
    {
        var native = light.NativePtr;
        if (native != null)
        {
            if (native->ProjectedCubemapTexture != null)
            {
                native->ProjectedCubemapTexture->DecRef();
                native->ProjectedCubemapTexture = null;
            }
            if (native->LightRenderObject != null)
                native->LightRenderObject->Texture = null;
        }
        light.SetGoboPath(null);
    }

    /// <summary>Records the texture a world light already projects, so the
    /// light that replaces it shows the same gobo. The texture doesn't have to
    /// be one of Poser's gobos.</summary>
    public void AdoptGobo(Light light, GameLight* original)
    {
        if (!AreGobosAvailable || original->ProjectedCubemapTexture == null || !SupportsGobo(light.Kind))
            return;
        var path = original->ProjectedCubemapTexture->FileName.ToString();
        if (!string.IsNullOrEmpty(path))
            light.SetGoboPath(path);
    }
}
