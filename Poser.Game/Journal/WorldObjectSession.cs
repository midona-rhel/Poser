using System.Numerics;
using Poser.Application.Transforms;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on a world object, as a journal
/// step. The one seam between the object pages and the handle.</summary>
public sealed class WorldObjectSession
{
    private readonly ValueJournal _journal;
    private readonly EntityValueJournal<IWorldObject> _values;

    public WorldObjectSession(ValueJournal journal, Scene.SceneLifecycleHistory? lifecycle = null)
    {
        _journal = journal;
        _values = new(journal, o => o.IsValid,
            lifecycle is null ? null : lifecycle.CurrentWorldObject);
    }

    /// <summary>Closes the open step; a new drag starts a new one.</summary>
    public void Seal() => _journal.Seal();

    private void Set<T>(IWorldObject worldObject, string property, string description,
        Func<IWorldObject, T> read, Action<IWorldObject, T> write, T value) =>
        _values.Set(worldObject, property, description, read, write, value);

    public void SetName(IWorldObject o, string value) =>
        Set(o, "Name", "Rename object", x => x.Name, (x, v) => x.Name = v, value);

    public void SetVisible(IWorldObject o, bool value) =>
        Set(o, "Visible", value ? "Show object" : "Hide object", x => x.Visible, (x, v) => x.Visible = v, value);

    public void SetOpacity(IWorldObject o, float value) =>
        Set(o, "Opacity", "Set object opacity", x => x.Opacity, (x, v) => x.Opacity = v, value);

    public void SetTint(IWorldObject o, Vector3? value) =>
        Set(o, "Tint", "Set object tint", x => x.Tint, (x, v) => x.Tint = v, value);

    public void SetStain(IWorldObject o, byte value) =>
        Set(o, "Stain", "Dye furniture", x => (x.Stain, x.Tint),
            (x, v) => { x.Tint = v.Item2; x.Stain = v.Item1; },
            (value, (Vector3?)null));

    public void SetFurnitureLight(IWorldObject o, string key, bool enabled)
    {
        var before = System.Linq.Enumerable.ToArray(o.FurnitureLights);
        var after = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(before,
            light => light.Key == key ? light with { Enabled = enabled } : light));
        _journal.Seal();
        Set(o, "FurnitureLights", "Set furniture light",
            x => System.Linq.Enumerable.ToArray(x.FurnitureLights),
            (x, value) => x.FurnitureLights = value, after);
        _journal.Seal();
    }

    public void SetNightState(IWorldObject o, bool value) =>
        Set(o, "Night", "Set object night state", x => x.NightState, (x, v) => x.NightState = v, value);

    public void SetAnimationPaused(IWorldObject o, bool value) =>
        o.AnimationPaused = value;

    public void SetLoopVfx(IWorldObject o, bool value) =>
        Set(o, "LoopVfx", "Set effect loop", x => x.LoopVfx, (x, v) => x.LoopVfx = v, value);

    public void SetVfxSpeed(IWorldObject o, float value) =>
        o.VfxSpeed = value;

    public void SetVfxPaused(IWorldObject o, bool value) =>
        o.VfxPaused = value;

    public void SetVfxIntensity(IWorldObject o, float value) =>
        Set(o, "VfxIntensity", "Set effect intensity", x => x.VfxIntensity, (x, v) => x.VfxIntensity = v, value);
}
