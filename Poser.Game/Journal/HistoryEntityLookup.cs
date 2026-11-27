using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Journal;

/// <summary>Finds which actor or bone an undo step belongs to when the step is
/// keyed by the actor or bone object itself, so "undo for the selected entity"
/// can find it.</summary>
public static class HistoryEntityLookup
{
    public static SelectionId? Identify(object owner, IEntityBindings bindings) => owner switch
    {
        IActor actor when bindings.GetActorId(actor) is { } id => SelectionId.ForActor(id),
        IBone bone when bindings.GetBoneId(bone) is { } id => SelectionId.ForBone(id),
        _ => null,
    };
}
