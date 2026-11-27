using System.Numerics;
using Poser.Application.Posing;
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
/// whatever body the actor has now, also after the actor was removed and
/// brought back by undo; these values are not part of the pose.
/// </summary>
public sealed class ActorValueSession(
    ValueJournal journal, ActorPresentationSession presentation,
    IEntityBindings bindings, IActorSpawnService spawns, IActorReplacements replacements) : IActorValueControl
{
    private const string Gone = "The actor is no longer available.";

    public void Seal() => journal.Seal();

    public PresentationReading? Read(ActorId actor) => presentation.Read(actor);
    public PresentationOverrides OverridesFor(ActorId actor) => presentation.OverridesFor(actor);

    // ── presentation ────────────────────────────────────────────────────
    public Outcome SetOpacity(ActorId actor, float value) =>
        SetValue(actor, "Opacity", "Set actor opacity",
            id => presentation.OverridesFor(id).Opacity ?? presentation.Read(id)?.Opacity ?? 1f,
            (id, x) => presentation.SetOpacity(id, x), value);

    public Outcome SetTint(ActorId actor, PresentationModel model, Vector4 value) =>
        SetValue(actor, model, "Set actor tint",
            id => CurrentTint(id, model),
            (id, x) => presentation.SetTint(id, model, x), value);

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
            id => presentation.OverridesFor(id).Wetness != null,
            (id, x) => presentation.SetWetnessEnabled(id, x), value);

    public Outcome SetWetness(ActorId actor, WetnessState value) =>
        SetValue(actor, "Wetness", "Set wetness",
            id => presentation.OverridesFor(id).Wetness ?? presentation.Read(id)?.Wetness ?? default,
            (id, x) => presentation.SetWetness(id, x), value);

    private Outcome SetValue<T>(ActorId actor, object property, string description,
        Func<ActorId, T> read, Func<ActorId, T, Outcome> write, T value)
    {
        if (!bindings.Resolve(actor).Success)
            return Outcome.Fail(Gone);
        var now = replacements.Track(actor);
        return journal.Set((actor, property), description,
            () => read(actor),
            x => now() is { } id ? write(id, x) : Outcome.Fail(Gone),
            value, () => now() is not null);
    }

    /// <summary>Puts back the actor's own opacity, tints, wetness and custom
    /// colours. Undo sets Poser's values again.</summary>
    public Outcome ResetPresentation(ActorId actor)
    {
        var before = presentation.OverridesFor(actor);
        var result = presentation.ResetActor(actor);
        if (!result.Success)
            return result;
        var now = replacements.Track(actor);
        journal.Record(SelectionId.ForActor(actor), "Reset appearance", before, (PresentationOverrides?)null,
            next => now() is { } id ? presentation.RestoreOverrides(id, next) : Outcome.Fail(Gone),
            () => now() is not null);
        return result;
    }

    // ── visibility ──────────────────────────────────────────────────────
    public bool? ReadVisibility(ActorId actor) =>
        bindings.Resolve(actor) is { Success: true, Value: { } live } ? spawns.IsVisible(live) : null;

    public Outcome SetVisibility(ActorId actor, bool visible)
    {
        if (ReadVisibility(actor) is not { } before)
            return Outcome.Fail(Gone);
        var now = replacements.Track(actor);
        return journal.Set(
            (actor, "Visible"),
            visible ? "Show actor" : "Hide actor",
            () => before,
            next => now() is { } id ? WriteVisibility(id, next) : Outcome.Fail(Gone),
            visible,
            () => now() is not null);
    }

    private Outcome WriteVisibility(ActorId actor, bool visible)
    {
        if (bindings.Resolve(actor) is not { Success: true, Value: { } live })
            return Outcome.Fail(Gone);
        spawns.SetVisibility(live, visible);
        return spawns.IsVisible(live) == visible
            ? Outcome.Ok()
            : Outcome.Fail("The game refused the visibility change.");
    }
}
