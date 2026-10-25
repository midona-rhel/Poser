using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Tracks the animation control and actor being scrubbed by one drag.</summary>
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
    /// Values captured when the drag starts. The selected layer is paused;
    /// without a layer selection, the whole actor is paused. The skeleton
    /// token lets the game port reject writes after the skeleton changes.
    /// </summary>
    private sealed record ScrubGesture(
        Guid Owner,
        ActorId Actor,
        ScrubControlId Control,
        float Duration,
        ulong Token,
        bool WasPaused);

    private ScrubGesture? _scrub;

    /// <summary>Seeks a layer by its own timeline clock, in seconds from its begin
    /// (a timeline still loading takes it once loaded).</summary>
    public Outcome SeekLayer(ActorId actor, AnimationSlot slot, float seconds) =>
        float.IsFinite(seconds)
            ? _port.SetControlTime(actor, ScrubControlId.Timeline(slot), Math.Max(seconds, 0f), 0)
            : Outcome.Fail("Scrub time must be a finite number.");

    /// <summary>Gets the control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _port.FindSlotControl(actor, slot, out _);

    /// <summary>
    /// Finds the animation control, pauses it, and remembers its duration
    /// and skeleton token. Returns an error if the control cannot be found.
    /// </summary>
    public Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null) =>
        BeginScrubCore(actor, control, owner, slot);

    private Outcome BeginScrubCore(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot)
    {
        var controls = _port.EnumerateControls(actor, out var token);
        ScrubControlReading? target = null;
        foreach (var reading in controls)
            if (reading.Id == control)
                target = reading;
        if (target == null)
            return Outcome.Fail("That animation control is no longer present.");

        // A layer's scrub holds that layer only; the others keep playing. Without a
        // known layer the whole actor is held.
        bool wasPaused = _speed.IsPaused(actor) ||
            (slot is { } held && _speed.IsSlotHeld(actor, held));
        if (!wasPaused)
        {
            var freeze = slot is { } layer
                ? _speed.PauseSlot(actor, layer)
                : _speed.SetSpeed(actor, 0f);
            if (!freeze.Success)
                return freeze;
        }

        _scrub = new ScrubGesture(owner, actor, control, target.Duration, token, wasPaused);
        return Outcome.Ok();
    }

    /// <summary>
    /// Seeks within the duration captured at Begin. A different actor or owner
    /// is rejected without changing the drag. A failed game write ends it.
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

    /// <summary>Forgets this owner's drag. The pause remains until a separate
    /// playback action releases it.</summary>
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
