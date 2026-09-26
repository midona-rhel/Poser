using Poser.Application.Transforms;
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
    ValueWriteResult SetWeight(ActorId actor, string unitId, float weight);
    ValueWriteResult SetPair(ActorId actor, string leftId, string rightId, float weight);
    ValueWriteResult Reset(ActorId actor);
    void Seal();
}

public interface IExpressionRuntimePort : IExpressionRead
{
    ValueWriteResult Write(ActorId actor, IReadOnlyList<(string Id, float Weight)> weights, bool reset);
}
