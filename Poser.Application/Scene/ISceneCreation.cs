using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Presentation;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Scene;

public sealed record ActorCreationRequest(
    bool ReserveCompanionSlot = false,
    int ModelCharaId = 0,
    SpawnCatalogEntry? Catalog = null);

public sealed record SceneCreationResult(SceneEntityHandle? Handle, string? Detail = null);

/// <summary>Creation retains native bodies in the runtime, never in deferred UI callbacks.</summary>
public interface ISceneCreation
{
    SceneCreationResult CreateActor(ActorCreationRequest request);
    SceneCreationResult CreateLight(LightKind kind);
    SceneCreationResult CreateLight(Poser.Files.LightFile document, string description);
    SceneCreationResult CreateCamera(CameraKind kind);
    SceneCreationResult CreateCamera(Poser.Files.CameraFile document, string description);
    SceneCreationResult CreateProp(PropModel? model = null);
    SceneCreationResult CreateOverlay(OverlayNodeKind kind);
    SceneCreationResult CreateCollider(IkColliderShape shape);
    SceneCreationResult CreateWorldObject(string path, PoseTransform placement);
    SceneCreationResult Duplicate(SelectionId source, bool withPose = false);
    SelectionId? Resolve(SceneEntityHandle handle, bool requirePose = false);
}
