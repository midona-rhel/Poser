using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Posing;

/// <summary>One history edit per expression gesture, independent of native/UI lifetime.</summary>
public sealed class ExpressionSession(ValueJournal journal, IExpressionRuntimePort runtime) : IExpressionControl
{
    public bool IsAvailable => runtime.IsAvailable;
    public bool HasActor(ActorId actor) => runtime.HasActor(actor);
    public IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> GetUnits(ActorId actor) => runtime.GetUnits(actor);
    public float GetWeight(ActorId actor, string unitId) => runtime.GetWeight(actor, unitId);
    public bool HasActiveExpression(ActorId actor) => runtime.HasActiveExpression(actor);
    public void Seal() => journal.Seal();

    private static ValueWriteResult Missing() => new(false, "The actor is no longer available.");

    public ValueWriteResult SetWeight(ActorId actor, string unitId, float weight) =>
        !HasActor(actor) ? Missing() : journal.TrySet((actor, unitId), "Set expression",
            () => runtime.GetWeight(actor, unitId),
            value => runtime.Write(actor, [(unitId, value)], reset: false),
            weight, () => HasActor(actor));

    public ValueWriteResult SetPair(ActorId actor, string leftId, string rightId, float weight) =>
        !HasActor(actor) ? Missing() : journal.TrySet((actor, leftId, rightId), "Set expression pair",
            () => (runtime.GetWeight(actor, leftId), runtime.GetWeight(actor, rightId)),
            pair => runtime.Write(actor, [(leftId, pair.Item1), (rightId, pair.Item2)], reset: false),
            (weight, weight), () => HasActor(actor));

    public ValueWriteResult Reset(ActorId actor)
    {
        if (!HasActor(actor)) return Missing();
        var before = runtime.GetUnits(actor)
            .Select(unit => (unit.Id, Weight: runtime.GetWeight(actor, unit.Id)))
            .Where(unit => unit.Weight != 0f).ToArray();
        if (before.Length == 0) return ValueWriteResult.Ok();
        var result = runtime.Write(actor, [], reset: true);
        if (result.Success)
            journal.RecordResult("Reset expression", before, Array.Empty<(string Id, float Weight)>(),
                weights => runtime.Write(actor, weights, reset: true), () => HasActor(actor));
        return result;
    }
}
