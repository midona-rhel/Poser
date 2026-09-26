using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Scene;

public sealed class ActorSceneControl(IFramework framework, IEntityBindings bindings,
    IActorManager actors, IActorSpawnService spawns) : IActorSceneControl
{
    private IActor? Resolve(ActorId id) => framework.IsInFrameworkUpdateThread
        && bindings.Resolve(id).Value is { } actor && bindings.GetActorId(actor) == id ? actor : null;

    public ActorSceneReading? Read(ActorId id) => Resolve(id) is { } actor
        ? new(spawns.GetSpawnedKind(actor), spawns.HasCompanionSlot(actor),
            spawns.IsSpawnedActor(actor) || spawns.RemovalRefusal(actor) == null) : null;

    public ValueWriteResult SetGameTarget(ActorId id)
    {
        if (Resolve(id) is not { } actor) return new(false, "The actor is no longer available.");
        actors.SetGPoseTarget(actor);
        return ValueWriteResult.Ok();
    }
}
