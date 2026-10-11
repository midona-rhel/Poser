using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Journal;

public static class HistoryEntityLookup
{
    public static SelectionId? Identify(object owner, IEntityBindings bindings) => owner switch
    {
        IActor actor when bindings.GetActorId(actor) is { } id => SelectionId.ForActor(id),
        IBone bone when bindings.GetBoneId(bone) is { } id => SelectionId.ForBone(id),
        IOverlayNode overlay when bindings.GetOverlayId(overlay) is { } id => SelectionId.ForOverlay(id),
        _ => null,
    };
}
