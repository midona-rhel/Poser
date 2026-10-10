using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;

namespace Poser.Application.Posing;

public interface IExpressionRead
{
    bool IsAvailable { get; }
    bool HasActor(ActorId actor);
    IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> GetUnits(ActorId actor);
    float GetWeight(ActorId actor, string unitId);
    bool HasActiveExpression(ActorId actor);
}

/// <summary>Expression controls and history use exact actor generations.</summary>
public interface IExpressionControl : IExpressionRead
{
    Outcome SetWeight(ActorId actor, string unitId, float weight);
    Outcome SetPair(ActorId actor, string leftId, string rightId, float weight);
    Outcome Reset(ActorId actor);
    void Seal();
}

public interface IExpressionRuntimePort : IExpressionRead
{
    Outcome Write(ActorId actor, IReadOnlyList<(string Id, float Weight)> weights, bool reset);
}
