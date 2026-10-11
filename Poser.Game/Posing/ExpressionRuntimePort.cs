using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

public sealed class ExpressionRuntimePort(
    IEntityBindings bindings, IExpressionService expressions) : IExpressionRuntimePort
{
    private IActor? Resolve(ActorId actor) => bindings.Resolve(actor).Value;

    public bool IsAvailable => expressions.IsAvailable;
    public bool HasActor(ActorId actor) => Resolve(actor) is not null;
    public IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> GetUnits(ActorId actor) =>
        Resolve(actor) is { } current ? expressions.GetUnits(current) : [];
    public float GetWeight(ActorId actor, string unitId) =>
        Resolve(actor) is { } current ? expressions.GetWeight(current, unitId) : 0;
    public bool HasActiveExpression(ActorId actor) =>
        Resolve(actor) is { } current && expressions.HasActiveExpression(current);

    public Outcome Write(ActorId actor, IReadOnlyList<(string Id, float Weight)> weights, bool reset)
    {
        if (Resolve(actor) is not { } current)
            return new(false, "The actor is no longer available.");
        if (weights.Any(unit => !float.IsFinite(unit.Weight)))
            return new(false, "Expression weights must be finite.");
        if (reset) expressions.ResetExpression(current);
        foreach (var (id, weight) in weights)
            expressions.SetWeight(current, id, weight);
        return Outcome.Ok();
    }
}
