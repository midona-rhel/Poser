using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// The per-actor <see cref="AnimationOverrides"/> the animation sub-sessions
/// share, plus the facial-bake command suspension every mutating verb checks.
/// An actor whose overrides become empty is forgotten.
/// </summary>
internal sealed class AnimationOverrideStore
{
    private readonly Dictionary<ActorId, AnimationOverrides> _overrides = new();

    /// <summary>Diagnostic tap for the pause/play path.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>True while a multi-phase operation owns the actor's animation.</summary>
    public bool CommandsSuspended { get; set; }

    public Outcome? Suspended() => CommandsSuspended
        ? Outcome.Fail("A face capture is in progress.")
        : null;

    public AnimationOverrides For(ActorId actor) =>
        _overrides.TryGetValue(actor, out var value) ? value : AnimationOverrides.None;

    public bool TryGet(ActorId actor, [MaybeNullWhen(false)] out AnimationOverrides owned) =>
        _overrides.TryGetValue(actor, out owned);

    public AnimationOverrides Mutate(ActorId actor, Func<AnimationOverrides, AnimationOverrides> change)
    {
        var updated = change(For(actor));
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        return updated;
    }

    public void Set(ActorId actor, AnimationOverrides owned) => _overrides[actor] = owned;

    public void Remove(ActorId actor) => _overrides.Remove(actor);

    public List<ActorId> Actors() => new(_overrides.Keys);
}
