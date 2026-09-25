using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Lights;

public sealed class LightControl(
    IEntityBindings bindings, IFramework framework,
    ILightingService lighting, LightSession values) : ILightControl
{
    public IReadOnlyList<LightGobo> Gobos { get; } = Array.AsReadOnly(
        lighting.Gobos.Select(g => new LightGobo(g.Path, g.Name)).ToArray());

    private ILight? Resolve(LightId id) => framework.IsInFrameworkUpdateThread &&
        bindings.Resolve(id) is { Success: true, Value: { IsValid: true } light } &&
        bindings.GetLightId(light) == id ? light : null;

    public LightReading? Read(LightId id) => Resolve(id) is { } l
        ? new(id, lighting.IsAvailable,
            l.Name,
            l.Kind,
            l.IsOn,
            l.Color,
            l.Intensity,
            l.Range,
            l.Falloff,
            l.FalloffType,
            l.SpotAngle,
            l.FalloffAngle,
            l.AreaAngle,
            l.HasReflection,
            l.CastsDynamicShadows,
            l.CastsCharacterShadow,
            l.CastsObjectShadow,
            l.CharacterShadowRange,
            l.ShadowPlaneNear,
            l.ShadowPlaneFar,
            l.Ownership, l.GoboPath, l.AttachedBone is not null,
            l.AttachedBone is { } bone ? bindings.GetBoneId(bone) : null)
        : null;

    public void Seal() => values.Seal();

    private ValueWriteResult Edit(LightId id, Action<ILight> write)
    {
        if (Resolve(id) is not { } light)
            return new(false, "The light is no longer available.");
        write(light);
        return ValueWriteResult.Ok();
    }

    public ValueWriteResult SetName(LightId id, string value) => Edit(id, l => values.SetName(l, value));
    public ValueWriteResult SetKind(LightId id, LightKind value) => Edit(id, l => values.SetKind(l, value));
    public ValueWriteResult SetIsOn(LightId id, bool value) => Edit(id, l => values.SetIsOn(l, value));
    public ValueWriteResult SetColor(LightId id, Vector3 value) => Edit(id, l => values.SetColor(l, value));
    public ValueWriteResult SetIntensity(LightId id, float value) => Edit(id, l => values.SetIntensity(l, value));
    public ValueWriteResult SetRange(LightId id, float value) => Edit(id, l => values.SetRange(l, value));
    public ValueWriteResult SetFalloff(LightId id, float value) => Edit(id, l => values.SetFalloff(l, value));
    public ValueWriteResult SetFalloffType(LightId id, LightFalloffType value) => Edit(id, l => values.SetFalloffType(l, value));
    public ValueWriteResult SetSpotAngle(LightId id, float value) => Edit(id, l => values.SetSpotAngle(l, value));
    public ValueWriteResult SetFalloffAngle(LightId id, float value) => Edit(id, l => values.SetFalloffAngle(l, value));
    public ValueWriteResult SetHasReflection(LightId id, bool value) => Edit(id, l => values.SetHasReflection(l, value));
    public ValueWriteResult SetCastsDynamicShadows(LightId id, bool value) => Edit(id, l => values.SetCastsDynamicShadows(l, value));
    public ValueWriteResult SetCastsCharacterShadow(LightId id, bool value) => Edit(id, l => values.SetCastsCharacterShadow(l, value));
    public ValueWriteResult SetCastsObjectShadow(LightId id, bool value) => Edit(id, l => values.SetCastsObjectShadow(l, value));
    public ValueWriteResult SetCharacterShadowRange(LightId id, float value) => Edit(id, l => values.SetCharacterShadowRange(l, value));
    public ValueWriteResult SetShadowPlaneNear(LightId id, float value) => Edit(id, l => values.SetShadowPlaneNear(l, value));
    public ValueWriteResult SetShadowPlaneFar(LightId id, float value) => Edit(id, l => values.SetShadowPlaneFar(l, value));
    // Change one component against the current value, not a retained UI snapshot.
    public ValueWriteResult SetAreaAngleX(LightId id, float value) =>
        Edit(id, l => values.SetAreaAngle(l, l.AreaAngle with { X = value }));
    public ValueWriteResult SetAreaAngleY(LightId id, float value) =>
        Edit(id, l => values.SetAreaAngle(l, l.AreaAngle with { Y = value }));

    public ValueWriteResult SetAttachedBone(LightId id, BoneId? target)
    {
        if (Resolve(id) is not { } light)
            return new(false, "The light is no longer available.");
        IBone? bone = null;
        if (target is { } boneId)
        {
            if (bindings.Resolve(boneId) is not { Success: true, Value: { Skeleton.IsValid: true } resolved } ||
                bindings.GetBoneId(resolved) != boneId)
                return new(false, "The bone is no longer available.");
            bone = resolved;
        }
        values.SetAttachedBone(light, bone);
        return ValueWriteResult.Ok();
    }

    public ValueWriteResult ApplyGobo(LightId id, uint index)
    {
        if (Resolve(id) is not { } light)
            return new(false, "The light is no longer available.");
        if (index >= Gobos.Count)
            return new(false, "The gobo is not available.");
        var gobo = Gobos[(int)index];
        return values.ApplyGobo(light, new(gobo.Path, gobo.Name))
            ? ValueWriteResult.Ok() : new(false, "The texture could not be applied.");
    }

    public ValueWriteResult ClearGobo(LightId id) => Edit(id, values.ClearGobo);
}
