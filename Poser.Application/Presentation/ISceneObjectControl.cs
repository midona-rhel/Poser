using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

public sealed record PropReading(PropId Id, string Name, bool Visible, PropModel Model, Vector4 Tint);

public sealed record WorldObjectReading(
    WorldObjectId Id, string Name, string Path, bool Spawned, bool Visible,
    bool IsVfx, bool IsFurniture, float Opacity, Vector3? Tint, bool? Dyeable,
    byte Stain, IReadOnlyList<FurnitureLightState> FurnitureLights, bool NightState,
    bool AnimationPaused, bool LoopVfx, float VfxSpeed, bool VfxPaused, float VfxIntensity);


/// <summary>Reads and changes the values of one prop, scenery object, furniture
/// piece or VFX, found by its id. A reading is a copy; changing it does nothing.
/// Respawning the object in the game and recording undo steps happen in here.</summary>
public interface ISceneObjectControl
{
    PropReading? Read(PropId id);
    WorldObjectReading? Read(WorldObjectId id);
    void Seal();
    Outcome Set<T>(PropId id, EntityProperty<PropId, T> property, T value);
    Outcome Update<T>(PropId id, EntityProperty<PropId, T> property, Func<T, T> change);
    Outcome Set<T>(WorldObjectId id, EntityProperty<WorldObjectId, T> property, T value);
    Outcome Update<T>(WorldObjectId id, EntityProperty<WorldObjectId, T> property, Func<T, T> change);

    /// <summary>Respawns the prop as <paramref name="model"/>. An undo step is
    /// recorded only if the respawn works.</summary>
    Outcome SetModel(PropId id, PropModel model);

    /// <summary>Turns one furniture light on or off as its own undo step.</summary>
    Outcome SetFurnitureLight(WorldObjectId id, string key, bool enabled);

    Task<Outcome> Respawn(WorldObjectId id, string path);
}
