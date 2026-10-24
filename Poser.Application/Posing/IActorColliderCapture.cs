using Poser.Application.Scene;
using Poser.Domain.Identity;

namespace Poser.Application.Posing;

public interface IActorColliderCapture
{
    bool Busy { get; }
    Task<ActorColliderCaptureResult> CreateAsync(ActorId actorId, string name);
}

public sealed record ActorColliderCaptureResult(SceneGroup Group, string? Warning);
