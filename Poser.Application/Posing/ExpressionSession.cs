using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;

namespace Poser.Application.Posing;

/// <summary>One history edit per expression gesture, independent of native/UI lifetime.</summary>
public sealed class ExpressionSession(ValueJournal journal, IExpressionRuntimePort runtime,
    IActorReplacements replacements) : IExpressionControl
{
    public bool IsAvailable => runtime.IsAvailable;
    public bool HasActor(ActorId actor) => runtime.HasActor(actor);
    public IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> GetUnits(ActorId actor) => runtime.GetUnits(actor);
    public float GetWeight(ActorId actor, string unitId) => runtime.GetWeight(actor, unitId);
    public bool HasActiveExpression(ActorId actor) => runtime.HasActiveExpression(actor);
    public void Seal() => journal.Seal();

    private static Outcome Missing() => new(false, "The actor is no longer available.");

    public Outcome SetWeight(ActorId actor, string unitId, float weight)
    {
        if (!HasActor(actor)) return Missing();
        var now = replacements.Track(actor);
        return journal.Set((actor, unitId), "Set expression",
            () => runtime.GetWeight(actor, unitId),
            value => now() is { } id ? runtime.Write(id, [(unitId, value)], reset: false) : Missing(),
            weight, () => now() is not null);
    }

    public Outcome SetPair(ActorId actor, string leftId, string rightId, float weight)
    {
        if (!HasActor(actor)) return Missing();
        var now = replacements.Track(actor);
        return journal.Set((actor, leftId, rightId), "Set expression pair",
            () => (runtime.GetWeight(actor, leftId), runtime.GetWeight(actor, rightId)),
            pair => now() is { } id
                ? runtime.Write(id, [(leftId, pair.Item1), (rightId, pair.Item2)], reset: false)
                : Missing(),
            (weight, weight), () => now() is not null);
    }

    public Outcome Reset(ActorId actor)
    {
        if (!HasActor(actor)) return Missing();
        var before = runtime.GetUnits(actor)
            .Select(unit => (unit.Id, Weight: runtime.GetWeight(actor, unit.Id)))
            .Where(unit => unit.Weight != 0f).ToArray();
        if (before.Length == 0) return Outcome.Ok();
        var now = replacements.Track(actor);
        var result = runtime.Write(actor, [], reset: true);
        if (result.Success)
            journal.Record(SelectionId.ForActor(actor), "Reset expression", before, Array.Empty<(string Id, float Weight)>(),
                weights => now() is { } id ? runtime.Write(id, weights, reset: true) : Missing(),
                () => now() is not null);
        return result;
    }
}
