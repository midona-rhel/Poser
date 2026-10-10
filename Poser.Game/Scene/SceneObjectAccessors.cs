using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Game.Presentation;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>How each declared prop and world-object property reads and
/// writes a live handle.</summary>
public static class SceneObjectAccessors
{
    public static EntityAccessors<PropId, IPropHandle> Props() =>
        new EntityAccessors<PropId, IPropHandle>("The prop did not accept the value.")
            .Assign(PropProperties.Name, p => p.Name, (p, v) => p.Name = v)
            .Assign(PropProperties.Visible, p => p.Visible, (p, v) => p.Visible = v)
            .Complete(PropProperties.All);

    public static EntityAccessors<WorldObjectId, IWorldObject> WorldObjects() =>
        new EntityAccessors<WorldObjectId, IWorldObject>("The object did not accept the value.")
            .Assign(WorldObjectProperties.Name, o => o.Name, (o, v) => o.Name = v)
            .Assign(WorldObjectProperties.Visible, o => o.Visible, (o, v) => o.Visible = v)
            .Assign(WorldObjectProperties.Opacity, o => o.Opacity, (o, v) => o.Opacity = v)
            .Assign(WorldObjectProperties.Tint, o => o.Tint, (o, v) => o.Tint = v)
            .Assign(WorldObjectProperties.NightState, o => o.NightState, (o, v) => o.NightState = v)
            .Assign(WorldObjectProperties.LoopVfx, o => o.LoopVfx, (o, v) => o.LoopVfx = v)
            .Assign(WorldObjectProperties.VfxIntensity, o => o.VfxIntensity, (o, v) => o.VfxIntensity = v)
            // The tint lands before the stain: the stain applies over the
            // cleared tint, and undo restores the tint first the same way.
            .Assign(WorldObjectProperties.Stain, o => (o.Stain, o.Tint), (o, v) =>
            {
                o.Tint = v.Tint;
                o.Stain = v.Stain;
            })
            .Assign(WorldObjectProperties.FurnitureLights, o => o.FurnitureLights.ToArray(),
                (o, v) => o.FurnitureLights = v)
            .Assign(WorldObjectProperties.AnimationPaused, o => o.AnimationPaused, (o, v) => o.AnimationPaused = v)
            .Assign(WorldObjectProperties.VfxSpeed, o => o.VfxSpeed, (o, v) => o.VfxSpeed = v)
            .Assign(WorldObjectProperties.VfxPaused, o => o.VfxPaused, (o, v) => o.VfxPaused = v)
            .Complete(WorldObjectProperties.All);
}
