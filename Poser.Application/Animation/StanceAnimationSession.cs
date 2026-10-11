using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Stance, weapon-draw and position-lock ownership.</summary>
internal sealed class StanceAnimationSession
{
    private readonly AnimationOverrideStore _store;
    private readonly IAnimationTimelinePort _timeline;
    private readonly IAnimationStancePort _port;
    private readonly BaseAnimationSession _base;

    public StanceAnimationSession(
        AnimationOverrideStore store, IAnimationTimelinePort timeline,
        IAnimationStancePort port, BaseAnimationSession baseSession)
    {
        _store = store;
        _timeline = timeline;
        _port = port;
        _base = baseSession;
    }

    /// <summary>False when the client's stance-transition functions were
    /// not found; the stance controls render disabled.</summary>
    public bool SupportsStance => _port.SupportsStance;

    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var capture = _store.For(actor).StanceCaptureValue;
        if (capture == null && _timeline.Read(actor) is { } reading)
            capture = new StanceCapture(reading.Stance, reading.Pose);

        // Stance playback stops repeat arms but keeps General repeat intent.
        var owned = _store.For(actor);
        bool wantsBaseLoop = owned.LoopWantedSlots.Contains(AnimationSlot.Base);
        if (owned.LoopedSlots.Count > 0 || owned.LoopWantedSlots.Count > 0)
        {
            if (owned.LoopedSlots.ContainsKey(AnimationSlot.Base))
            {
                var cleared = _timeline.SetForceLoop(actor, 0);
                if (!cleared.Success)
                    return cleared;
            }
            _timeline.ClearLoops(actor);
            _store.Mutate(actor, o => o with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
                LoopWantedSlots = wantsBaseLoop
                    ? new HashSet<AnimationSlot> { AnimationSlot.Base }
                    : new HashSet<AnimationSlot>(),
            });
            owned = _store.For(actor);
        }
        if (owned.BaseCapture != null || owned.BaseTimeline != null)
        {
            var released = _base.ResetBaseSelection(actor, preserveLoopIntent: true);
            if (!released.Success)
                return released;
        }

        var result = _port.SetStance(actor, stance, pose);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { StanceCaptureValue = o.StanceCaptureValue ?? capture });
        return Outcome.Ok();
    }

    public Outcome SetWeaponDrawn(ActorId actor, bool drawn)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var capture = _store.For(actor).WeaponCapture;
        if (capture == null && _timeline.Read(actor) is { } reading)
            capture = reading.WeaponDrawn;

        var result = _port.SetWeaponDrawn(actor, drawn);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { WeaponCapture = o.WeaponCapture ?? capture });
        return Outcome.Ok();
    }

    public Outcome SetPositionLock(ActorId actor, bool locked)
    {
        var result = _port.SetPositionLock(actor, locked);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { PositionLock = locked });
        return Outcome.Ok();
    }
}
