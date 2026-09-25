using System.Numerics;
using Poser.Application.Transforms;
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
    ValueWriteResult SetName(PropId id, string value);
    ValueWriteResult SetVisible(PropId id, bool value);
    ValueWriteResult SetModel(PropId id, PropModel value);
    ValueWriteResult SetName(WorldObjectId id, string value);
    ValueWriteResult SetVisible(WorldObjectId id, bool value);
    ValueWriteResult SetOpacity(WorldObjectId id, float value);
    ValueWriteResult SetTint(WorldObjectId id, Vector3? value);
    ValueWriteResult SetStain(WorldObjectId id, byte value);
    ValueWriteResult SetFurnitureLight(WorldObjectId id, string key, bool value);
    ValueWriteResult SetNightState(WorldObjectId id, bool value);
    ValueWriteResult SetAnimationPaused(WorldObjectId id, bool value);
    ValueWriteResult SetLoopVfx(WorldObjectId id, bool value);
    ValueWriteResult SetVfxSpeed(WorldObjectId id, float value);
    ValueWriteResult SetVfxPaused(WorldObjectId id, bool value);
    ValueWriteResult SetVfxIntensity(WorldObjectId id, float value);
    Task<ValueWriteResult> Respawn(WorldObjectId id, string path);
}
