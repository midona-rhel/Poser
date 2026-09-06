using System.Numerics;
using Poser.Application.Transforms;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on a world object, as a journal
/// step. The one seam between the object pages and the handle.</summary>
public sealed class WorldObjectSession
{
    private readonly ValueJournal _journal;

    public WorldObjectSession(ValueJournal journal) => _journal = journal;

    /// <summary>Closes the open step; a new drag starts a new one.</summary>
    public void Seal() => _journal.Seal();

    public void SetName(IWorldObject o, string value) =>
        _journal.Set((o, "Name"), "Rename object", () => o.Name, v => o.Name = v, value, () => o.IsValid);

    public void SetVisible(IWorldObject o, bool value) =>
        _journal.Set((o, "Visible"), value ? "Show object" : "Hide object", () => o.Visible, v => o.Visible = v, value, () => o.IsValid);

    public void SetOpacity(IWorldObject o, float value) =>
        _journal.Set((o, "Opacity"), "Set object opacity", () => o.Opacity, v => o.Opacity = v, value, () => o.IsValid);

    public void SetTint(IWorldObject o, Vector3? value) =>
        _journal.Set((o, "Tint"), "Set object tint", () => o.Tint, v => o.Tint = v, value, () => o.IsValid);

    public void SetStain(IWorldObject o, byte value) =>
        _journal.Set((o, "Stain"), "Dye furniture", () => (o.Stain, o.Tint),
            v => { o.Tint = v.Item2; o.Stain = v.Item1; },
            (value, (Vector3?)null), () => o.IsValid);

    public void SetNightState(IWorldObject o, bool value) =>
        _journal.Set((o, "Night"), "Set object night state", () => o.NightState, v => o.NightState = v, value, () => o.IsValid);

    public void SetAnimationPaused(IWorldObject o, bool value) =>
        o.AnimationPaused = value;

    public void SetLoopVfx(IWorldObject o, bool value) =>
        _journal.Set((o, "LoopVfx"), "Set effect loop", () => o.LoopVfx, v => o.LoopVfx = v, value, () => o.IsValid);

    public void SetVfxSpeed(IWorldObject o, float value) =>
        o.VfxSpeed = value;

    public void SetVfxPaused(IWorldObject o, bool value) =>
        o.VfxPaused = value;

    public void SetVfxIntensity(IWorldObject o, float value) =>
        _journal.Set((o, "VfxIntensity"), "Set effect intensity", () => o.VfxIntensity, v => o.VfxIntensity = v, value, () => o.IsValid);
}
