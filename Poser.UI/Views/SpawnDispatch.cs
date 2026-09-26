using Poser.Domain.Companions;
using Poser.Domain.Scene;
using Poser.Library;

namespace Poser.UI.Views;

public enum SpawnActionId
{
    NewActor,
    NewActorCompanion,
    CloneActor,
    CloneActorPosed,
    ActorFromMcdf,
    ActorFromLibrary,
    ActorFromFile,
    Prop,
    PropFromLibrary,
    PropFromFile,
    ObjectFromLibrary,
    ObjectFromFile,
    VfxFromLibrary,
    VfxFromFile,
    OverlayTalk,
    OverlayBalloon,
    OverlayStatus,
    ReferenceImage,
    OverlayFromLibrary,
    OverlayFromFile,
    LightSpot,
    LightPoint,
    LightArea,
    LightDirectional,
    LightFromLibrary,
    LightFromFile,
    CameraGame,
    CameraFree,
    CameraFromLibrary,
    CameraFromFile,
    FurnitureFromLibrary,
    FurnitureFromFile,
    ColliderPlane,
    ColliderBox,
    ColliderCylinder,
    ColliderCone,
    ColliderCapsule,
    ColliderSphere,
}

public enum SpawnSource { Action, SavedGroup, Library, Catalog }

/// <summary>Dispatch data travels with each row, independent of filtering and catalog sizes.</summary>
public abstract record SpawnDispatch
{
    public sealed record Builtin(SpawnActionId Id) : SpawnDispatch;
    public sealed record Actor(SpawnCatalogEntry Entry) : SpawnDispatch;
    public sealed record Npc(int ModelCharaId) : SpawnDispatch;
    public sealed record Prop(PropModel Model) : SpawnDispatch;
    public sealed record WorldAsset(string Path) : SpawnDispatch;
    public sealed record Saved(string Label, string Path, PoseLibraryEntryKind Kind) : SpawnDispatch;
}
