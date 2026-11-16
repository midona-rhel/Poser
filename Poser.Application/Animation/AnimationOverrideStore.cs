using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// What Poser has changed in each actor's animation, shared by the animation
/// sessions. Mutate removes actors with no remaining overrides.
/// </summary>
internal sealed class AnimationOverrideStore
{
    private readonly Dictionary<ActorId, AnimationOverrides> _overrides = new();

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
        return updated;
    }

    public void Set(ActorId actor, AnimationOverrides owned) => _overrides[actor] = owned;

    public void Remove(ActorId actor) => _overrides.Remove(actor);

    public List<ActorId> Actors() => new(_overrides.Keys);
}
