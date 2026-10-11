using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Game.Presentation;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Lighting;

public sealed class LightControl : ILightControl
{
    private const string Unavailable = "The light is no longer available.";
    private readonly IEntityBindings _bindings;
    private readonly ILightingService _lighting;
    private readonly TransformParenting _parenting;
    private readonly EntityValues<LightId> _values;

    public LightControl(IEntityBindings bindings, ILightingService lighting,
        ValueJournal journal, TransformParenting parenting, IEntityHistoryResolver<ILight>? history = null)
    {
        _bindings = bindings;
        _lighting = lighting;
        _parenting = parenting;
        Gobos = Array.AsReadOnly(lighting.Gobos.Select(g => new LightGobo(g.Path, g.Name)).ToArray());
        _values = new(journal, new HandleValuePort<LightId, ILight>(Resolve, SelectionId.ForLight,
            light => light.IsValid, history, LightAccessors.Create(lighting), Unavailable), Unavailable);
    }

    public IReadOnlyList<LightGobo> Gobos { get; }

    private ILight? Resolve(LightId id) =>
        _bindings.Resolve(id) is { Success: true, Value: { IsValid: true } light } ? light : null;

    public LightReading? Read(LightId id) => Resolve(id) is { } l
        ? new(id, _lighting.IsAvailable,
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
            l.Ownership, l.GoboPath, _parenting.Read(SelectionId.ForLight(id)) is not null,
            _parenting.Read(SelectionId.ForLight(id))?.Target.Bone)
        : null;

    public void Seal() => _values.Seal();

    public Outcome Set<T>(LightId id, EntityProperty<LightId, T> property, T value) =>
        _values.Set(id, property, value);

    public Outcome Update<T>(LightId id, EntityProperty<LightId, T> property, Func<T, T> change) =>
        _values.Update(id, property, change);

    public Outcome ApplyGobo(LightId id, uint index)
    {
        if (Resolve(id) is null)
            return new(false, Unavailable);
        if (index >= Gobos.Count)
            return new(false, "The gobo is not available.");
        return _values.Set(id, LightProperties.Gobo, Gobos[(int)index].Path);
    }
}
