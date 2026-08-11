using System;
using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// .poserlight import/export. Export snapshots the live light; import spawns
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

    public bool ExportLight(ILight light, string path)
    {
        try
        {
            var lightFile = CreateLightFile(light);
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

    public ILight? ImportLight(string path)
    {
        try
        {
            var lightFile = LightFile.Load(path);
            if (lightFile == null)
            {
                _log.Error($"Failed to load light file from {path}");
                return null;
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

    private static LightFile CreateLightFile(ILight light) => new()
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

    private static void Apply(LightFile lightFile, ILight light)
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
