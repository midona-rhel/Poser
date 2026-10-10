using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

public sealed class ActorSceneControl(IEntityBindings bindings,
    IActorManager actors, IActorSpawnService spawns) : IActorSceneControl
{
    private IActor? Resolve(ActorId id) => bindings.Resolve(id).Value;

    public ActorSceneReading? Read(ActorId id) => Resolve(id) is { } actor
        ? new(spawns.GetSpawnedKind(actor), spawns.HasCompanionSlot(actor),
            spawns.IsSpawnedActor(actor) || spawns.RemovalRefusal(actor) == null) : null;

    public Outcome SetGameTarget(ActorId id)
    {
        if (Resolve(id) is not { } actor) return new(false, "The actor is no longer available.");
        actors.SetGPoseTarget(actor);
        return Outcome.Ok();
    }
}
