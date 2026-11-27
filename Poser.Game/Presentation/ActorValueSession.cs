using System.Numerics;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Services;

namespace Poser.Game.Presentation;

/// <summary>
/// Opacity, tint, wetness and visibility commands for one actor, each as one
/// undo step. The Appearance tab uses all of them; the context menus, the
/// sidebar, the selection section and group gates use visibility. A slider
/// drag is one step for the whole drag. Undo writes the value back to
/// whatever body the actor has now; these values are not part of the pose.
/// </summary>
public sealed class ActorValueSession(
    ValueJournal journal, ActorPresentationSession presentation,
    IEntityBindings bindings, IActorSpawnService spawns) : IActorValueControl
{
    public void Seal() => journal.Seal();

    public PresentationReading? Read(ActorId actor) => presentation.Read(actor);
    public PresentationOverrides OverridesFor(ActorId actor) => presentation.OverridesFor(actor);

    private bool Alive(ActorId actor) => bindings.Resolve(actor).Success;

    // ── presentation ────────────────────────────────────────────────────
    public Outcome SetOpacity(ActorId actor, float value) =>
        SetValue(actor, "Opacity", "Set actor opacity",
            () => presentation.OverridesFor(actor).Opacity ?? presentation.Read(actor)?.Opacity ?? 1f,
            x => presentation.SetOpacity(actor, x), value);

    public Outcome SetTint(ActorId actor, PresentationModel model, Vector4 value) =>
        SetValue(actor, model, "Set actor tint",
            () => CurrentTint(actor, model),
            x => presentation.SetTint(actor, model, x), value);

    private Vector4 CurrentTint(ActorId actor, PresentationModel model)
    {
        if (presentation.OverridesFor(actor).Tints.TryGetValue(model, out var owned))
            return owned;
        var reading = presentation.Read(actor);
        return model switch
        {
            PresentationModel.MainHand => reading?.MainHandTint ?? Vector4.One,
            PresentationModel.OffHand => reading?.OffHandTint ?? Vector4.One,
            _ => reading?.CharacterTint ?? Vector4.One,
        };
    }

    public Outcome SetWetnessEnabled(ActorId actor, bool value) =>
        SetValue(actor, "WetnessEnabled", value ? "Hold wetness" : "Release wetness",
            () => presentation.OverridesFor(actor).Wetness != null,
            x => presentation.SetWetnessEnabled(actor, x), value);

    public Outcome SetWetness(ActorId actor, WetnessState value) =>
        SetValue(actor, "Wetness", "Set wetness",
            () => presentation.OverridesFor(actor).Wetness ?? presentation.Read(actor)?.Wetness ?? default,
            x => presentation.SetWetness(actor, x), value);

    private Outcome SetValue<T>(ActorId actor, object property, string description,
        Func<T> read, Func<T, Outcome> write, T value)
    {
        if (!Alive(actor))
            return Outcome.Fail("The actor is no longer available.");
        return journal.Set((actor, property), description, read, write, value, () => Alive(actor));
    }

    /// <summary>Puts back the actor's own opacity, tints, wetness and custom
    /// colours. Undo sets Poser's values again.</summary>
    public Outcome ResetPresentation(ActorId actor)
    {
        var before = presentation.OverridesFor(actor);
        var result = presentation.ResetActor(actor);
        if (!result.Success)
            return result;
        journal.Record(SelectionId.ForActor(actor), "Reset appearance", before, (PresentationOverrides?)null,
            next => presentation.RestoreOverrides(actor, next), () => Alive(actor));
        return result;
    }

    // ── visibility ──────────────────────────────────────────────────────
    public bool? ReadVisibility(ActorId actor) =>
        bindings.Resolve(actor) is { Success: true, Value: { } live } ? spawns.IsVisible(live) : null;

    public Outcome SetVisibility(ActorId actor, bool visible)
    {
        if (ReadVisibility(actor) is not { } before)
            return new(false, "The actor is no longer available.");
        return journal.Set(
            (actor, "Visible"),
            visible ? "Show actor" : "Hide actor",
            () => before,
            next => WriteVisibility(actor, next),
            visible,
            () => Alive(actor));
    }

    private Outcome WriteVisibility(ActorId actor, bool visible)
    {
        if (bindings.Resolve(actor) is not { Success: true, Value: { } live })
            return new(false, "The actor is no longer available.");
        spawns.SetVisibility(live, visible);
        return spawns.IsVisible(live) == visible
            ? Outcome.Ok()
            : new(false, "The game refused the visibility change.");
    }
}
