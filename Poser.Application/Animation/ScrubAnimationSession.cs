using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>The single in-flight scrub drag.</summary>
internal sealed class ScrubAnimationSession
{
    private readonly IAnimationScrubPort _port;
    private readonly SpeedAnimationSession _speed;

    public ScrubAnimationSession(IAnimationScrubPort port, SpeedAnimationSession speed)
    {
        _port = port;
        _speed = speed;
    }

    /// <summary>
    /// One scrub drag. Everything that could move under the drag freezes
    /// at Begin: playback (so the game cannot advance the frame out from
    /// under the pointer), the control identity, its duration, and the
    /// skeleton token. Release leaves the actor paused on the frame the
    /// user chose — resuming is a separate, deliberate act.
    /// </summary>
    private sealed record ScrubGesture(
        Guid Owner,
        ActorId Actor,
        ScrubControlId Control,
        float Duration,
        ulong Token,
        bool WasPaused);

    private ScrubGesture? _scrub;

    /// <summary>Gets the control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _port.FindSlotControl(actor, slot, out _);

    /// <summary>
    /// Freezes playback and captures the drag's whole mapping. Fails when
    /// the control is not present, so a scrub never starts against
    /// geometry that is already gone.
    /// </summary>
    public Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner) =>
        BeginScrubCore(actor, control, owner);

    private Outcome BeginScrubCore(ActorId actor, ScrubControlId control, Guid owner)
    {
        var controls = _port.EnumerateControls(actor, out var token);
        ScrubControlReading? target = null;
        foreach (var reading in controls)
            if (reading.Id == control)
                target = reading;
        if (target == null)
            return Outcome.Fail("That animation control is no longer present.");

        bool wasPaused = _speed.IsPaused(actor);
        if (!wasPaused)
        {
            var freeze = _speed.SetSpeed(actor, 0f);
            if (!freeze.Success)
                return freeze;
        }

        _scrub = new ScrubGesture(owner, actor, control, target.Duration, token, wasPaused);
        return Outcome.Ok();
    }

    /// <summary>
    /// Writes a frame clamped to the duration captured at Begin. Actor and
    /// skeleton mismatches end the drag instead of retargeting the write.
    /// </summary>
    public Outcome UpdateScrub(ActorId actor, float time, Guid owner) =>
        UpdateScrubCore(actor, time, owner);

    private Outcome UpdateScrubCore(ActorId actor, float time, Guid owner)
    {
        if (_scrub is not { } gesture)
            return Outcome.Fail("No scrub is active.");
        if (gesture.Owner != owner)
            return Outcome.Fail("The scrub in flight belongs to another control.");
        if (!gesture.Actor.Equals(actor))
            return Outcome.Fail(
                "The scrub in flight belongs to a different actor.");
        if (!float.IsFinite(time))
            return Outcome.Fail("Scrub time must be a finite number.");

        float clamped = Math.Clamp(time, 0f, gesture.Duration);
        var result = _port.SetControlTime(
            gesture.Actor, gesture.Control, clamped, gesture.Token);
        if (result.Success)
            return Outcome.Ok();

        _scrub = null;
        return result;
    }

    /// <summary>Ends the drag, leaving the actor paused on the released
    /// frame. That pause is an ordinary speed override, so Resume
    /// continues from exactly there.</summary>
    public void EndScrub(Guid owner)
    {
        if (_scrub?.Owner != owner)
            return;
        _scrub = null;
    }

    /// <summary>Ends a drag whose actor the scene no longer contains.</summary>
    public void Forget(IReadOnlySet<ActorId> present)
    {
        if (_scrub is { } scrub && !present.Contains(scrub.Actor)) _scrub = null;
    }
}
