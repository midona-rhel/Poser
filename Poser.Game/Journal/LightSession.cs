using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on a light, as a journal step. The
/// gobo and the attached bone go through here too.</summary>
public sealed class LightSession
{
    private readonly ValueJournal _journal;
    private readonly ILightingService _lighting;

    public LightSession(ValueJournal journal, ILightingService lighting)
    {
        _journal = journal;
        _lighting = lighting;
    }

    public void Seal() => _journal.Seal();

    private void Set<T>(ILight l, string property, string description, Func<T> read, Action<T> write, T value) =>
        _journal.Set((l, property), description, read, write, value, () => l.IsValid);

    public void SetName(ILight l, string v) => Set(l, "Name", "Rename light", () => l.Name, x => l.Name = x, v);
    public void SetKind(ILight l, LightKind v) => Set(l, "Kind", "Set light type", () => l.Kind, x => l.Kind = x, v);
    public void SetIsOn(ILight l, bool v) => Set(l, "IsOn", v ? "Switch light on" : "Switch light off", () => l.IsOn, x => l.IsOn = x, v);
    public void SetColor(ILight l, Vector3 v) => Set(l, "Color", "Set light colour", () => l.Color, x => l.Color = x, v);
    public void SetIntensity(ILight l, float v) => Set(l, "Intensity", "Set light intensity", () => l.Intensity, x => l.Intensity = x, v);
    public void SetRange(ILight l, float v) => Set(l, "Range", "Set light range", () => l.Range, x => l.Range = x, v);
    public void SetFalloff(ILight l, float v) => Set(l, "Falloff", "Set light falloff", () => l.Falloff, x => l.Falloff = x, v);
    public void SetFalloffType(ILight l, LightFalloffType v) => Set(l, "FalloffType", "Set light falloff type", () => l.FalloffType, x => l.FalloffType = x, v);
    public void SetSpotAngle(ILight l, float v) => Set(l, "SpotAngle", "Set cone angle", () => l.SpotAngle, x => l.SpotAngle = x, v);
    public void SetFalloffAngle(ILight l, float v) => Set(l, "FalloffAngle", "Set falloff angle", () => l.FalloffAngle, x => l.FalloffAngle = x, v);
    public void SetAreaAngle(ILight l, Vector2 v) => Set(l, "AreaAngle", "Set panel angle", () => l.AreaAngle, x => l.AreaAngle = x, v);
    public void SetHasReflection(ILight l, bool v) => Set(l, "HasReflection", "Set light reflections", () => l.HasReflection, x => l.HasReflection = x, v);
    public void SetCastsDynamicShadows(ILight l, bool v) => Set(l, "CastsDynamicShadows", "Set dynamic shadows", () => l.CastsDynamicShadows, x => l.CastsDynamicShadows = x, v);
    public void SetCastsCharacterShadow(ILight l, bool v) => Set(l, "CastsCharacterShadow", "Set character shadows", () => l.CastsCharacterShadow, x => l.CastsCharacterShadow = x, v);
    public void SetCastsObjectShadow(ILight l, bool v) => Set(l, "CastsObjectShadow", "Set object shadows", () => l.CastsObjectShadow, x => l.CastsObjectShadow = x, v);
    public void SetCharacterShadowRange(ILight l, float v) => Set(l, "CharacterShadowRange", "Set character shadow range", () => l.CharacterShadowRange, x => l.CharacterShadowRange = x, v);
    public void SetShadowPlaneNear(ILight l, float v) => Set(l, "ShadowPlaneNear", "Set shadow near plane", () => l.ShadowPlaneNear, x => l.ShadowPlaneNear = x, v);
    public void SetShadowPlaneFar(ILight l, float v) => Set(l, "ShadowPlaneFar", "Set shadow far plane", () => l.ShadowPlaneFar, x => l.ShadowPlaneFar = x, v);
    public void SetAttachedBone(ILight l, IBone? v) => Set(l, "AttachedBone", v is null ? "Detach light" : "Attach light", () => l.AttachedBone, x => l.AttachedBone = x, v);

    /// <summary>Projects the gobo; false when the texture could not be
    /// applied, and nothing is journaled then.</summary>
    public bool ApplyGobo(ILight l, GoboEntry gobo)
    {
        var before = Current(l);
        if (!_lighting.ApplyGobo(l, gobo))
            return false;
        _journal.Record("Set gobo", before, gobo, next => Put(l, next), () => l.IsValid);
        return true;
    }

    public void ClearGobo(ILight l)
    {
        var before = Current(l);
        if (before is null)
            return;
        _lighting.ClearGobo(l);
        _journal.Record("Clear gobo", before, (GoboEntry?)null, next => Put(l, next), () => l.IsValid);
    }

    private GoboEntry? Current(ILight l)
    {
        if (l.GoboPath is not { } path)
            return null;
        foreach (var entry in _lighting.Gobos)
            if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                return entry;
        return new GoboEntry(path, path);
    }

    private void Put(ILight l, GoboEntry? gobo)
    {
        if (gobo is null)
            _lighting.ClearGobo(l);
        else
            _lighting.ApplyGobo(l, gobo);
    }
}
