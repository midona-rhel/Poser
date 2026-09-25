using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Held timeline expressions and capture, distinct from authored expression weights.</summary>
public interface IExpressionPreview
{
    event Action<string>? Failed;
    bool IsPending(ActorId actor);
    bool IsBaking { get; }
    AnimationResult Choose(ActorId actor, TimelineEntry entry);
    AnimationResult Preview(ActorId actor, ushort timeline);
    AnimationResult Reset(ActorId actor);
    AnimationResult Bake(ActorId actor, ushort timeline);
    void CancelRetry(ActorId actor);
}
