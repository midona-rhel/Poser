using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

/// <summary>Every value a surface sets on a world object. A dye is the stain
/// and tint together, so clearing the tint with it is one step. Animation
/// pause and effect speed and pause are transport: they write but never
/// journal.</summary>
public static class WorldObjectProperties
{
    public static readonly EntityProperty<WorldObjectId, string> Name = new("Name", "Rename object");
    public static readonly EntityProperty<WorldObjectId, bool> Visible = new("Visible", on => on ? "Show object" : "Hide object");
    public static readonly EntityProperty<WorldObjectId, float> Opacity = new("Opacity", "Set object opacity");
    public static readonly EntityProperty<WorldObjectId, Vector3?> Tint = new("Tint", "Set object tint");
    public static readonly EntityProperty<WorldObjectId, bool> NightState = new("NightState", "Set object night state");
    public static readonly EntityProperty<WorldObjectId, bool> LoopVfx = new("LoopVfx", "Set effect loop");
    public static readonly EntityProperty<WorldObjectId, float> VfxIntensity = new("VfxIntensity", "Set effect intensity");
    public static readonly EntityProperty<WorldObjectId, (byte Stain, Vector3? Tint)> Stain = new("Stain", "Dye furniture");
    public static readonly EntityProperty<WorldObjectId, FurnitureLightState[]> FurnitureLights = new("FurnitureLights", "Set furniture light");
    public static readonly EntityProperty<WorldObjectId, bool> AnimationPaused = new("AnimationPaused", "Pause animation", PropertyHistory.Transport);
    public static readonly EntityProperty<WorldObjectId, float> VfxSpeed = new("VfxSpeed", "Set effect speed", PropertyHistory.Transport);
    public static readonly EntityProperty<WorldObjectId, bool> VfxPaused = new("VfxPaused", "Pause effect", PropertyHistory.Transport);

    public static readonly IReadOnlyList<EntityProperty> All =
    [
        Name, Visible, Opacity, Tint, NightState, LoopVfx, VfxIntensity, Stain, FurnitureLights,
        AnimationPaused, VfxSpeed, VfxPaused,
    ];
}
