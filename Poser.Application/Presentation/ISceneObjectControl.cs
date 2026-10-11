using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

public sealed record PropReading(PropId Id, string Name, bool Visible, PropModel Model);

public sealed record WorldObjectReading(
    WorldObjectId Id, string Name, string Path, bool Spawned, bool Visible,
    bool IsVfx, bool IsFurniture, float Opacity, Vector3? Tint, bool? Dyeable,
    byte Stain, IReadOnlyList<FurnitureLightState> FurnitureLights, bool NightState,
    bool AnimationPaused, bool LoopVfx, float VfxSpeed, bool VfxPaused, float VfxIntensity);


/// <summary>Exact-id property access for props, scenery, furniture and VFX.
/// Reads are detached; native replacement and value history stay behind this boundary.</summary>
public interface ISceneObjectControl
{
    PropReading? Read(PropId id);
    WorldObjectReading? Read(WorldObjectId id);
    void Seal();
    Outcome Set<T>(PropId id, EntityProperty<PropId, T> property, T value);
    Outcome Update<T>(PropId id, EntityProperty<PropId, T> property, Func<T, T> change);
    Outcome Set<T>(WorldObjectId id, EntityProperty<WorldObjectId, T> property, T value);
    Outcome Update<T>(WorldObjectId id, EntityProperty<WorldObjectId, T> property, Func<T, T> change);

    /// <summary>Respawns the prop as <paramref name="model"/>; only a landed
    /// respawn is a step.</summary>
    Outcome SetModel(PropId id, PropModel model);

    /// <summary>Switches one furniture light as its own discrete step.</summary>
    Outcome SetFurnitureLight(WorldObjectId id, string key, bool enabled);

    Task<Outcome> Respawn(WorldObjectId id, string path);
}
