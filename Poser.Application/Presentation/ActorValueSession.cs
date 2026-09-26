using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

/// <summary>
/// The actor-level values a surface sets — presentation (opacity, tints,
/// wetness) and visibility — as journal
/// steps. These carry no pose snapshot: they are not bone state, and they
/// re-apply to whatever body the actor has when undone.
/// </summary>
public sealed class ActorValueSession : IActorValueControl
{
    private readonly ValueJournal _journal;
    private readonly ActorPresentationSession _presentation;
    private readonly IActorValueRuntime _runtime;

    public ActorValueSession(
        ValueJournal journal,
        ActorPresentationSession presentation,
        IActorValueRuntime runtime)
    {
        _journal = journal;
        _presentation = presentation;
        _runtime = runtime;
    }

    public void Seal() => _journal.Seal();

    private bool Alive(ActorId actor) => _runtime.IsResolvable(actor);

    // ── presentation ────────────────────────────────────────────────────
    public PresentationResult SetOpacity(ActorId actor, float value) =>
        SetValue(actor, "Opacity", "Set actor opacity",
            () => _presentation.OverridesFor(actor).Opacity ?? _presentation.Read(actor)?.Opacity ?? 1f,
            x => _presentation.SetOpacity(actor, x), value);

    public PresentationResult SetTint(ActorId actor, PresentationModel model, Vector4 value) =>
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

    public PresentationResult SetWetnessEnabled(ActorId actor, bool value) =>
        SetValue(actor, "WetnessEnabled", value ? "Hold wetness" : "Release wetness",
            () => _presentation.OverridesFor(actor).Wetness != null,
            x => _presentation.SetWetnessEnabled(actor, x), value);

    public PresentationResult SetWetness(ActorId actor, WetnessState value) =>
        SetValue(actor, "Wetness", "Set wetness",
            () => _presentation.OverridesFor(actor).Wetness ?? _presentation.Read(actor)?.Wetness ?? default,
            x => _presentation.SetWetness(actor, x), value);

    private PresentationResult SetValue<T>(ActorId actor, object property, string description,
        Func<T> read, Func<T, PresentationResult> write, T value)
    {
        if (!Alive(actor))
            return PresentationResult.Fail("The actor is no longer available.");
        var result = _journal.TrySet((actor, property), description, read, next =>
        {
            var applied = write(next);
            return new ValueWriteResult(applied.Success, applied.Detail);
        }, value, () => Alive(actor));
        return new(result.Success, result.Detail);
    }

    /// <summary>Hands every presentation value back; the step's undo
    /// re-applies each value that was held.</summary>
    public PresentationResult ResetPresentation(ActorId actor)
    {
        var before = _presentation.OverridesFor(actor);
        var result = _presentation.ResetActor(actor);
        if (!result.Success)
            return result;
        _journal.RecordResult("Reset appearance", before, (PresentationOverrides?)null, next =>
        {
            var restored = _presentation.RestoreOverrides(actor, next);
            return new ValueWriteResult(restored.Success, restored.Detail);
        }, () => Alive(actor));
        return result;
    }

    // ── visibility ──────────────────────────────────────────────────────
    public bool? ReadVisibility(ActorId actor) => _runtime.ReadVisibility(actor);

    public ValueWriteResult SetVisibility(ActorId actor, bool visible)
    {
        if (ReadVisibility(actor) is not { } before)
            return new(false, "The actor is no longer available.");
        return _journal.TrySet(
            (actor, "Visible"),
            visible ? "Show actor" : "Hide actor",
            () => before,
            next => _runtime.SetVisibility(actor, next),
            visible,
            () => Alive(actor));
    }
}
