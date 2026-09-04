using System.Numerics;
using Poser.Application.Appearance;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>
/// The actor-level values a surface sets — presentation (opacity, tints,
/// wetness), the model id, visibility and the companion — as journal
/// steps. These carry no pose snapshot: they are not bone state, and they
/// re-apply to whatever body the actor has when undone.
/// </summary>
public sealed class ActorValueSession
{
    private readonly ValueJournal _journal;
    private readonly ActorPresentationSession _presentation;
    private readonly ActorModelIdSession _model;
    private readonly IActorSpawnService _spawns;
    private readonly IEntityBindings _bindings;

    public ActorValueSession(
        ValueJournal journal,
        ActorPresentationSession presentation,
        ActorModelIdSession model,
        IActorSpawnService spawns,
        IEntityBindings bindings)
    {
        _journal = journal;
        _presentation = presentation;
        _model = model;
        _spawns = spawns;
        _bindings = bindings;
    }

    public void Seal() => _journal.Seal();

    private bool Alive(ActorId actor) => _bindings.Resolve(actor).Success;

    // ── presentation ────────────────────────────────────────────────────
    public PresentationResult SetOpacity(ActorId actor, float value)
    {
        PresentationResult result = PresentationResult.Ok();
        _journal.Set((actor, "Opacity"), "Set actor opacity",
            () => _presentation.OverridesFor(actor).Opacity ?? _presentation.Read(actor)?.Opacity ?? 1f,
            x => result = _presentation.SetOpacity(actor, x), value, () => Alive(actor));
        return result;
    }

    public PresentationResult SetTint(ActorId actor, PresentationModel model, Vector4 value)
    {
        PresentationResult result = PresentationResult.Ok();
        _journal.Set((actor, model), "Set actor tint",
            () => CurrentTint(actor, model),
            x => result = _presentation.SetTint(actor, model, x), value, () => Alive(actor));
        return result;
    }

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

    public PresentationResult SetWetnessEnabled(ActorId actor, bool value)
    {
        PresentationResult result = PresentationResult.Ok();
        _journal.Set((actor, "WetnessEnabled"), value ? "Hold wetness" : "Release wetness",
            () => _presentation.OverridesFor(actor).Wetness != null,
            x => result = _presentation.SetWetnessEnabled(actor, x), value, () => Alive(actor));
        return result;
    }

    public PresentationResult SetWetness(ActorId actor, WetnessState value)
    {
        PresentationResult result = PresentationResult.Ok();
        _journal.Set((actor, "Wetness"), "Set wetness",
            () => _presentation.OverridesFor(actor).Wetness ?? _presentation.Read(actor)?.Wetness ?? default,
            x => result = _presentation.SetWetness(actor, x), value, () => Alive(actor));
        return result;
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

    // ── model id ────────────────────────────────────────────────────────
    public PresentationResult ApplyModelId(ActorId actor, int modelCharaId)
    {
        var before = _model.IsOwned(actor) ? _model.Read(actor) : null;
        var result = _model.Apply(actor, modelCharaId);
        if (!result.Success)
            return result;
        _journal.Record("Set model id", before, (int?)modelCharaId, PutModel(actor), () => Alive(actor));
        return result;
    }

    public PresentationResult ResetModelId(ActorId actor)
    {
        var before = _model.IsOwned(actor) ? _model.Read(actor) : null;
        var result = _model.Reset(actor);
        if (!result.Success)
            return result;
        _journal.Record("Reset model id", before, (int?)null, PutModel(actor), () => Alive(actor));
        return result;
    }

    private Action<int?> PutModel(ActorId actor) => next =>
    {
        if (next is { } id)
            _model.Apply(actor, id);
        else
            _model.Reset(actor);
    };

    // ── visibility and companion ────────────────────────────────────────
    public void SetVisibility(IActor actor, bool visible)
    {
        var id = _bindings.GetActorId(actor);
        _journal.Set((actor, "Visible"), visible ? "Show actor" : "Hide actor",
            () => _spawns.IsVisible(actor), x => _spawns.SetVisibility(actor, x), visible,
            () => id is { } actorId && Alive(actorId));
    }

    public bool SetCompanion(IActor owner, CompanionAttachment? attachment)
    {
        var before = _spawns.GetCompanionInfo(owner);
        if (!_spawns.SetCompanion(owner, attachment))
            return false;
        var id = _bindings.GetActorId(owner);
        _journal.Record(attachment is null ? "Remove companion" : "Set companion", before, attachment,
            next => _spawns.SetCompanion(owner, next), () => id is { } actorId && Alive(actorId));
        return true;
    }
}
