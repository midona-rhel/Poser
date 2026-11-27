using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>Reset → All in the inspector: puts one actor back the way the game had it.</summary>
public interface IActorResetControl
{
    PoseEditResult ResetAll(ActorId actor);
}
