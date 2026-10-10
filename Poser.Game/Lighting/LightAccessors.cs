using System.Numerics;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Presentation;
using Poser.Services;

namespace Poser.Game.Lighting;

/// <summary>How each declared light property reads and writes a live light.</summary>
public static class LightAccessors
{
    // Render-object setters silently skip a light without a render object;
    // reading back turns that into a refusal instead of a phantom step.
    private const string NotApplied = "The light did not accept the value.";

    public static EntityAccessors<LightId, ILight> Create(ILightingService lighting) =>
        new EntityAccessors<LightId, ILight>(NotApplied)
            .Assign(LightProperties.Name, l => l.Name, (l, v) => l.Name = v)
            // The getter reads visibility the native update derives later.
            .Assign(LightProperties.IsOn, l => l.IsOn, (l, v) => l.IsOn = v)
            .Assign(LightProperties.Kind, l => l.Kind, (l, v) => l.Kind = v, verify: true)
            .Assign(LightProperties.Color, l => l.Color, (l, v) => l.Color = v, verify: true)
            .Assign(LightProperties.Intensity, l => l.Intensity, (l, v) => l.Intensity = v, verify: true)
            .Assign(LightProperties.Range, l => l.Range, (l, v) => l.Range = v, verify: true)
            .Assign(LightProperties.Falloff, l => l.Falloff, (l, v) => l.Falloff = v, verify: true)
            .Assign(LightProperties.FalloffType, l => l.FalloffType, (l, v) => l.FalloffType = v, verify: true)
            .Assign(LightProperties.SpotAngle, l => l.SpotAngle, (l, v) => l.SpotAngle = v, verify: true)
            .Assign(LightProperties.FalloffAngle, l => l.FalloffAngle, (l, v) => l.FalloffAngle = v, verify: true)
            // Stored in radians, so the degree read-back is compared with a tolerance.
            .Map(LightProperties.AreaAngle, l => l.AreaAngle, (l, v) =>
            {
                l.AreaAngle = v;
                return Vector2.DistanceSquared(l.AreaAngle, v) < 1e-6f ? ValueWriteResult.Ok() : new(false, NotApplied);
            })
            .Assign(LightProperties.HasReflection, l => l.HasReflection, (l, v) => l.HasReflection = v, verify: true)
            .Assign(LightProperties.CastsDynamicShadows, l => l.CastsDynamicShadows, (l, v) => l.CastsDynamicShadows = v, verify: true)
            .Assign(LightProperties.CastsCharacterShadow, l => l.CastsCharacterShadow, (l, v) => l.CastsCharacterShadow = v, verify: true)
            .Assign(LightProperties.CastsObjectShadow, l => l.CastsObjectShadow, (l, v) => l.CastsObjectShadow = v, verify: true)
            .Assign(LightProperties.CharacterShadowRange, l => l.CharacterShadowRange, (l, v) => l.CharacterShadowRange = v, verify: true)
            .Assign(LightProperties.ShadowPlaneNear, l => l.ShadowPlaneNear, (l, v) => l.ShadowPlaneNear = v, verify: true)
            .Assign(LightProperties.ShadowPlaneFar, l => l.ShadowPlaneFar, (l, v) => l.ShadowPlaneFar = v, verify: true)
            .Map(LightProperties.Gobo, l => l.GoboPath, (l, path) => ApplyGobo(lighting, l, path))
            .Complete(LightProperties.All);

    private static ValueWriteResult ApplyGobo(ILightingService lighting, ILight light, string? path)
    {
        if (path is null)
        {
            lighting.ClearGobo(light);
            return ValueWriteResult.Ok();
        }
        var gobo = lighting.Gobos.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? new GoboEntry(path, path);
        return lighting.ApplyGobo(light, gobo) ? ValueWriteResult.Ok() : new(false, "The texture could not be applied.");
    }
}
