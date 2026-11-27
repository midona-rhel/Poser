using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Finds an actor again after undo removed it and brought it back. It keeps
/// the actor's game object from when the step was recorded and asks the actor
/// undo slots which object replaced it, the way undo steps for lights, cameras
/// and overlays do. The history is created on first use, because the undo
/// slots are built from services that themselves record undo steps here.
/// </summary>
public sealed class ActorReplacements(IEntityBindings bindings, Lazy<IEntityHistoryResolver<IActor>> history)
    : IActorReplacements
{
    public Func<ActorId?> Track(ActorId actor)
    {
        var original = bindings.Resolve(actor).Value;
        return () => original is not null
            && history.Value.Resolve(original) is { } current
            && bindings.GetActorId(current) is { } id
            && bindings.Resolve(id).Success
                ? id
                : null;
    }
}
