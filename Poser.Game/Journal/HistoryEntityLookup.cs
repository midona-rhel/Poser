using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

public static class HistoryEntityLookup
{
    public static SelectionId? Identify(object owner, IEntityBindings bindings) => owner switch
    {
        IActor actor when bindings.GetActorId(actor) is { } id => SelectionId.ForActor(id),
        IBone bone when bindings.GetBoneId(bone) is { } id => SelectionId.ForBone(id),
        ILight light when bindings.GetLightId(light) is { } id => SelectionId.ForLight(id),
        IVirtualCamera camera when bindings.GetCameraId(camera) is { } id => SelectionId.ForCamera(id),
        IPropHandle prop when bindings.GetPropId(prop) is { } id => SelectionId.ForProp(id),
        IWorldObject world when bindings.GetWorldObjectId(world) is { } id => SelectionId.ForWorldObject(id),
        IOverlayNode overlay when bindings.GetOverlayId(overlay) is { } id => SelectionId.ForOverlay(id),
        _ => null,
    };
}
