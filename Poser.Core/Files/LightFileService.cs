using System;
using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// .xivl import/export. Export snapshots the live light; import spawns
/// a light of the file's kind and writes the file's every property onto it.
/// </summary>
public class LightFileService : ILightFileService
{
    private readonly IPluginLog _log;
    private readonly ILightingService _lighting;

    public LightFileService(
        IPluginLog log,
        ILightingService lighting)
    {
        _log = log;
        _lighting = lighting;
    }

    public bool ExportLight(ILight light, string path) =>
        ExportLight(light, path, null, null);

    public bool ExportLight(
        ILight light,
        string path,
        PlacementAnchorData? cameraAnchor,
        PlacementAnchorData? actorAnchor)
    {
        try
        {
            var lightFile = CreateLightFile(light);
            lightFile.CameraAnchor = cameraAnchor;
            lightFile.ActorAnchor = actorAnchor;
            if (lightFile.Save(path))
            {
                _log.Debug($"Exported light '{light.Name}' to {path}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to export light: {ex.Message}");
            return false;
        }
    }

    public ILight? ImportLight(string path) =>
        ImportLight(path, ObjectPlacementMode.AsSaved, default, 0f, out _);

    /// <summary>
    /// Import with a placement: the file's transform is rebased from its
    /// saved anchor onto the caller's current one BEFORE the light exists,
    /// so the spawn lands placed rather than jumping. A mode whose anchor
    /// the file does not record refuses by name and spawns nothing.
    /// </summary>
    public ILight? ImportLight(
        string path,
        ObjectPlacementMode mode,
        System.Numerics.Vector3 currentPosition,
        float currentYaw,
        out string? refusal)
    {
        refusal = null;
        try
        {
            var lightFile = LightFile.Load(path);
            if (lightFile == null)
            {
                _log.Error($"Failed to load light file from {path}");
                refusal = "The light file could not be read.";
                return null;
            }

            if (mode != ObjectPlacementMode.AsSaved)
            {
                var anchor = mode == ObjectPlacementMode.RelativeToCamera
                    ? lightFile.CameraAnchor
                    : lightFile.ActorAnchor;
                if (anchor is null)
                {
                    refusal = mode == ObjectPlacementMode.RelativeToCamera
                        ? "This entry records no camera anchor, so it " +
                          "cannot be placed relative to the camera. Load " +
                          "it as saved instead."
                        : "This entry records no actor anchor (nothing was " +
                          "selected when it was saved), so it cannot be " +
                          "placed relative to an actor. Load it as saved " +
                          "instead.";
                    return null;
                }
                ObjectPlacement.Rebase(
                    lightFile.Transform, anchor, currentPosition, currentYaw);
            }

            // Kind is set at spawn AND written again below: the spawn kind
            // decides which native light is created, the property write is
            // what a file saved from a runtime-switched light needs.
            var light = _lighting.SpawnLight(lightFile.Kind);
            if (light == null)
            {
                _log.Error("Failed to spawn a light for the imported file");
                return null;
            }

            Apply(lightFile, light);
            ApplyGobo(lightFile, light);
            return light;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to import light: {ex.Message}");
            return null;
        }
    }

    /// <summary>The ONE ILight → LightFile mapping; scene capture reuses it
    /// so a scene light and a .xivl are the same document.</summary>
    internal static LightFile CreateLightFile(ILight light) => new()
    {
        Name = light.Name,
        Kind = light.Kind,
        IsOn = light.IsOn,
        Transform = light.Transform,
        Color = light.Color,
        Intensity = light.Intensity,
        Range = light.Range,
        Falloff = light.Falloff,
        FalloffType = light.FalloffType,
        SpotAngle = light.SpotAngle,
        FalloffAngle = light.FalloffAngle,
        AreaAngle = light.AreaAngle,
        HasReflection = light.HasReflection,
        CastsDynamicShadows = light.CastsDynamicShadows,
        CastsCharacterShadow = light.CastsCharacterShadow,
        CastsObjectShadow = light.CastsObjectShadow,
        CharacterShadowRange = light.CharacterShadowRange,
        ShadowPlaneNear = light.ShadowPlaneNear,
        ShadowPlaneFar = light.ShadowPlaneFar,
        Gobo = light.GoboPath,
    };

    /// <summary>Resolves the saved path against the live gobo library — a
    /// path the running client no longer ships is dropped rather than pushed
    /// at the game.</summary>
    private void ApplyGobo(LightFile lightFile, ILight light)
    {
        if (string.IsNullOrEmpty(lightFile.Gobo))
            return;

        foreach (var gobo in _lighting.Gobos)
        {
            if (!string.Equals(gobo.Path, lightFile.Gobo, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_lighting.ApplyGobo(light, gobo))
                _log.Warning($"Could not apply gobo '{gobo.Name}' to '{light.Name}'");
            return;
        }

        _log.Warning($"Light file references an unknown gobo: {lightFile.Gobo}");
    }

    /// <summary>The ONE LightFile → ILight property application; scene load
    /// reuses it (gobo resolution stays with the callers' services).</summary>
    internal static void Apply(LightFile lightFile, ILight light)
    {
        light.Name = lightFile.Name;
        light.Kind = lightFile.Kind;
        light.IsOn = lightFile.IsOn;
        light.Transform = lightFile.Transform;
        light.Color = lightFile.Color;
        light.Intensity = lightFile.Intensity;
        light.Range = lightFile.Range;
        light.Falloff = lightFile.Falloff;
        light.FalloffType = lightFile.FalloffType;
        light.SpotAngle = lightFile.SpotAngle;
        light.FalloffAngle = lightFile.FalloffAngle;
        light.AreaAngle = lightFile.AreaAngle;
        light.HasReflection = lightFile.HasReflection;
        light.CastsDynamicShadows = lightFile.CastsDynamicShadows;
        light.CastsCharacterShadow = lightFile.CastsCharacterShadow;
        light.CastsObjectShadow = lightFile.CastsObjectShadow;
        light.CharacterShadowRange = lightFile.CharacterShadowRange;
        light.ShadowPlaneNear = lightFile.ShadowPlaneNear;
        light.ShadowPlaneFar = lightFile.ShadowPlaneFar;
    }
}
