using System.Numerics;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Services;

namespace Poser.Game.Presentation;

/// <summary>
/// The Appearance tab's opacity, tint, wetness and visibility commands, each
/// as one undo step. A slider drag is one step for the whole drag. Undo writes
/// the value back to whatever body the actor has now; these values are not
/// part of the pose.
/// </summary>
public sealed class ActorValueSession(
    ValueJournal journal, ActorPresentationSession presentation,
    IEntityBindings bindings, IActorSpawnService spawns) : IActorValueControl
{
    private readonly ValueJournal _journal = journal;
    private readonly ActorPresentationSession _presentation = presentation;

    public void Seal() => _journal.Seal();

    public bool IsSupported(ActorId actor) => _presentation.IsSupported(actor);
    public PresentationReading? Read(ActorId actor) => _presentation.Read(actor);
    public PresentationOverrides OverridesFor(ActorId actor) => _presentation.OverridesFor(actor);

    private bool Alive(ActorId actor) => bindings.Resolve(actor).Success;

    // ── presentation ────────────────────────────────────────────────────
    public Outcome SetOpacity(ActorId actor, float value) =>
        SetValue(actor, "Opacity", "Set actor opacity",
            () => _presentation.OverridesFor(actor).Opacity ?? _presentation.Read(actor)?.Opacity ?? 1f,
            x => _presentation.SetOpacity(actor, x), value);

    public Outcome SetTint(ActorId actor, PresentationModel model, Vector4 value) =>
        SetValue(actor, model, "Set actor tint",
            () => CurrentTint(actor, model),
            x => _presentation.SetTint(actor, model, x), value);

    private Vector4 CurrentTint(ActorId actor, PresentationModel model)
    {
        if (_presentation.OverridesFor(actor).Tints.TryGetValue(model, out var owned))
            return owned;
        var reading = _presentation.Read(actor);
        return model switch
        {
            PresentationModel.MainHand => reading?.MainHandTint ?? Vector4.One,
            PresentationModel.OffHand => reading?.OffHandTint ?? Vector4.One,
            _ => reading?.CharacterTint ?? Vector4.One,
        };
    }

    public Outcome SetWetnessEnabled(ActorId actor, bool value) =>
        SetValue(actor, "WetnessEnabled", value ? "Hold wetness" : "Release wetness",
            () => _presentation.OverridesFor(actor).Wetness != null,
            x => _presentation.SetWetnessEnabled(actor, x), value);

    public Outcome SetWetness(ActorId actor, WetnessState value) =>
        SetValue(actor, "Wetness", "Set wetness",
            () => _presentation.OverridesFor(actor).Wetness ?? _presentation.Read(actor)?.Wetness ?? default,
            x => _presentation.SetWetness(actor, x), value);

    private Outcome SetValue<T>(ActorId actor, object property, string description,
        Func<T> read, Func<T, Outcome> write, T value)
    {
        if (!Alive(actor))
            return Outcome.Fail("The actor is no longer available.");
        return _journal.Set((actor, property), description, read, write, value, () => Alive(actor));
    }

    /// <summary>Puts back the actor's own opacity, tints, wetness and colours.
    /// Undo sets Poser's values again.</summary>
    public Outcome ResetPresentation(ActorId actor)
    {
        var before = _presentation.OverridesFor(actor);
        var result = _presentation.ResetActor(actor);
        if (!result.Success)
            return result;
        _journal.Record(SelectionId.ForActor(actor), "Reset appearance", before, (PresentationOverrides?)null, next =>
        {
            var restored = _presentation.RestoreOverrides(actor, next);
            return new Outcome(restored.Success, restored.Detail);
        }, () => Alive(actor));
        return result;
    }

    // ── visibility ──────────────────────────────────────────────────────
    public bool? ReadVisibility(ActorId actor) =>
        bindings.Resolve(actor) is { Success: true, Value: { } live } ? spawns.IsVisible(live) : null;

    public Outcome SetVisibility(ActorId actor, bool visible)
    {
        if (ReadVisibility(actor) is not { } before)
            return new(false, "The actor is no longer available.");
        return _journal.Set(
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
