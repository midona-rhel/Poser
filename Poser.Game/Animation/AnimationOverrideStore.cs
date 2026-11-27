using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

using Poser.Application.Animation;

namespace Poser.Game.Animation;

/// <summary>
/// What Poser has changed in each actor's animation. This is the only copy:
/// the animation sessions change it, and the game side reads it to hold
/// speeds and weights every frame. Mutate removes actors with no remaining
/// overrides.
/// </summary>
public sealed class AnimationOverrideStore
{
    private readonly Dictionary<ActorId, AnimationOverrides> _overrides = new();

    /// <summary>Raised after any actor's overrides change.</summary>
    public event Action? Changed;

    /// <summary>Diagnostic tap for the pause/play path.</summary>
    public Action<string>? Trace { get; set; }

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
        Changed?.Invoke();
        return updated;
    }

    public void Remove(ActorId actor)
    {
        if (_overrides.Remove(actor))
            Changed?.Invoke();
    }

    public List<ActorId> Actors() => new(_overrides.Keys);
}
