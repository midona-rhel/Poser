using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Services;

namespace Poser.Game.Presentation;

public sealed class ActorValueRuntime(IEntityBindings bindings, IActorSpawnService spawns) : IActorValueRuntime
{
    public bool IsResolvable(ActorId actor) => bindings.Resolve(actor).Success;

    public bool? ReadVisibility(ActorId actor)
    {
        var resolved = bindings.Resolve(actor);
        return resolved.Success && resolved.Value is { } live ? spawns.IsVisible(live) : null;
    }

    public ValueWriteResult SetVisibility(ActorId actor, bool visible)
    {
        var resolved = bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } live)
            return new(false, "The actor is no longer available.");
        spawns.SetVisibility(live, visible);
        return spawns.IsVisible(live) == visible
            ? ValueWriteResult.Ok()
            : new(false, "The game refused the visibility change.");
    }
}
