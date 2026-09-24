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
    private readonly Scene.SceneLifecycleHistory? _lifecycle;

    public LightSession(ValueJournal journal, ILightingService lighting, Scene.SceneLifecycleHistory? lifecycle = null)
    {
        _journal = journal;
        _lighting = lighting;
        _lifecycle = lifecycle;
    }

    public void Seal() => _journal.Seal();

    private ILight? Live(ILight light) => _lifecycle is null ? light : _lifecycle.CurrentLight(light);

    private void Set<T>(ILight l, string property, string description, Func<ILight, T> read, Action<ILight, T> write, T value)
    {
        if (!l.IsValid) return;
        _journal.Set((l, property), description, () => read(l),
            next => { if (Live(l) is { IsValid: true } current) write(current, next); },
            value, () => Live(l) is { IsValid: true });
    }

    public void SetName(ILight l, string v) => Set(l, "Name", "Rename light", x => x.Name, (x, value) => x.Name = value, v);
    public void SetKind(ILight l, LightKind v) => Set(l, "Kind", "Set light type", x => x.Kind, (x, value) => x.Kind = value, v);
    public void SetIsOn(ILight l, bool v) => Set(l, "IsOn", v ? "Switch light on" : "Switch light off", x => x.IsOn, (x, value) => x.IsOn = value, v);
    public void SetColor(ILight l, Vector3 v) => Set(l, "Color", "Set light colour", x => x.Color, (x, value) => x.Color = value, v);
    public void SetIntensity(ILight l, float v) => Set(l, "Intensity", "Set light intensity", x => x.Intensity, (x, value) => x.Intensity = value, v);
    public void SetRange(ILight l, float v) => Set(l, "Range", "Set light range", x => x.Range, (x, value) => x.Range = value, v);
    public void SetFalloff(ILight l, float v) => Set(l, "Falloff", "Set light falloff", x => x.Falloff, (x, value) => x.Falloff = value, v);
    public void SetFalloffType(ILight l, LightFalloffType v) => Set(l, "FalloffType", "Set light falloff type", x => x.FalloffType, (x, value) => x.FalloffType = value, v);
    public void SetSpotAngle(ILight l, float v) => Set(l, "SpotAngle", "Set cone angle", x => x.SpotAngle, (x, value) => x.SpotAngle = value, v);
    public void SetFalloffAngle(ILight l, float v) => Set(l, "FalloffAngle", "Set falloff angle", x => x.FalloffAngle, (x, value) => x.FalloffAngle = value, v);
    public void SetAreaAngle(ILight l, Vector2 v) => Set(l, "AreaAngle", "Set panel angle", x => x.AreaAngle, (x, value) => x.AreaAngle = value, v);
    public void SetHasReflection(ILight l, bool v) => Set(l, "HasReflection", "Set light reflections", x => x.HasReflection, (x, value) => x.HasReflection = value, v);
    public void SetCastsDynamicShadows(ILight l, bool v) => Set(l, "CastsDynamicShadows", "Set dynamic shadows", x => x.CastsDynamicShadows, (x, value) => x.CastsDynamicShadows = value, v);
    public void SetCastsCharacterShadow(ILight l, bool v) => Set(l, "CastsCharacterShadow", "Set character shadows", x => x.CastsCharacterShadow, (x, value) => x.CastsCharacterShadow = value, v);
    public void SetCastsObjectShadow(ILight l, bool v) => Set(l, "CastsObjectShadow", "Set object shadows", x => x.CastsObjectShadow, (x, value) => x.CastsObjectShadow = value, v);
    public void SetCharacterShadowRange(ILight l, float v) => Set(l, "CharacterShadowRange", "Set character shadow range", x => x.CharacterShadowRange, (x, value) => x.CharacterShadowRange = value, v);
    public void SetShadowPlaneNear(ILight l, float v) => Set(l, "ShadowPlaneNear", "Set shadow near plane", x => x.ShadowPlaneNear, (x, value) => x.ShadowPlaneNear = value, v);
    public void SetShadowPlaneFar(ILight l, float v) => Set(l, "ShadowPlaneFar", "Set shadow far plane", x => x.ShadowPlaneFar, (x, value) => x.ShadowPlaneFar = value, v);
    public void SetAttachedBone(ILight l, IBone? v) => Set(l, "AttachedBone", v is null ? "Detach light" : "Attach light", x => x.AttachedBone, (x, value) => x.AttachedBone = value, v);

    /// <summary>Projects the gobo; false when the texture could not be
    /// applied, and nothing is journaled then.</summary>
    public bool ApplyGobo(ILight l, GoboEntry gobo)
    {
        var before = Current(l);
        if (!_lighting.ApplyGobo(l, gobo))
            return false;
        _journal.Record("Set gobo", before, gobo, next => PutCurrent(l, next), () => Live(l) is { IsValid: true });
        return true;
    }

    public void ClearGobo(ILight l)
    {
        var before = Current(l);
        if (before is null)
            return;
        _lighting.ClearGobo(l);
        _journal.Record("Clear gobo", before, (GoboEntry?)null, next => PutCurrent(l, next), () => Live(l) is { IsValid: true });
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

    private void PutCurrent(ILight original, GoboEntry? gobo)
    {
        if (Live(original) is not { IsValid: true } l) return;
        if (gobo is null)
            _lighting.ClearGobo(l);
        else
            _lighting.ApplyGobo(l, gobo);
    }
}
