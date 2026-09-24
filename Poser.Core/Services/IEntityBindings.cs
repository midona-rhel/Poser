using Poser.Domain.Identity;
using Poser.Entities;

namespace Poser.Services;

/// <summary>Stable ids to live entities and back. The one lookup every
/// surface resolves through; the registry behind it decides what a
/// generation change means.</summary>
public interface IEntityBindings
{
    ActorId? GetActorId(IActor actor);
    BoneId? GetBoneId(IBone bone);
    LightId? GetLightId(ILight light);
    CameraId? GetCameraId(IVirtualCamera camera);
    PropId? GetPropId(IPropHandle prop);
    WorldObjectId? GetWorldObjectId(IWorldObject worldObject);
    OverlayId? GetOverlayId(IOverlayNode overlay);

    BindingResult<IActor> Resolve(ActorId id);
    BindingResult<IBone> Resolve(BoneId id);
    BindingResult<ILight> Resolve(LightId id);
    BindingResult<IVirtualCamera> Resolve(CameraId id);
    BindingResult<IPropHandle> Resolve(PropId id);
    BindingResult<IWorldObject> Resolve(WorldObjectId id);
    BindingResult<IOverlayNode> Resolve(OverlayId id);
    ISkeleton? ResolveSkeleton(SkeletonId id);
}
