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

/// <summary>The handle of the new entity, or null with the reason it could
/// not be added.</summary>
public sealed record SceneCreationResult(SceneEntityHandle? Handle, string? Detail = null);

/// <summary>Adds actors, lights, cameras, objects and overlays to the scene. The
/// new game object is held by the game side, never by a window waiting to
/// finish the job.</summary>
public interface ISceneCreation
{
    bool IsAvailable(SceneEntityKind kind);
    SceneCreationResult CreateActor(ActorCreationRequest request);
    SceneCreationResult CreateLight(LightKind kind);
    SceneCreationResult CreateCamera(CameraKind kind);
    SceneCreationResult CreateProp(PropModel? model = null);
    SceneCreationResult CreateOverlay(OverlayNodeKind kind);
    SceneCreationResult CreateCollider(IkColliderShape shape);
    SceneCreationResult CreateWorldObject(string path);
    SceneCreationResult Duplicate(SelectionId source, bool withPose = false);
    IReadOnlyDictionary<SelectionId, SceneCreationResult> DuplicateMany(IReadOnlyList<SelectionId> sources, bool withPose = false);
    SelectionId? Resolve(SceneEntityHandle handle, bool requirePose = false);
}
