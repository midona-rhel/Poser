using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Held timeline expressions and capture, distinct from authored expression weights.</summary>
public interface IExpressionPreview
{
    event Action<string>? Failed;
    bool IsPending(ActorId actor);
    bool IsBaking { get; }
    Outcome Choose(ActorId actor, TimelineEntry entry);
    Outcome Preview(ActorId actor, ushort timeline);
    Outcome Reset(ActorId actor);
    Outcome Bake(ActorId actor, ushort timeline);
    void CancelRetry(ActorId actor);
}
