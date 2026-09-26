using Poser.Application.Scene;
using Poser.Domain.Identity;

namespace Poser.Application.Posing;

public interface IActorColliderCapture
{
    bool Busy { get; }
    Task<SceneGroup> CreateAsync(ActorId actorId, string name);
}
