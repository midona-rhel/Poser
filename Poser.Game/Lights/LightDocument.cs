using Poser.Entities;
using Poser.Files;

namespace Poser.Game.Lights;

/// <summary>Shared file, scene and lifecycle mapping; native values never enter presentation.</summary>
internal static class LightDocument
{
    internal static LightFile Capture(ILight light) => new()
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
