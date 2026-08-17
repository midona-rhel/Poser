using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Animation;

public readonly record struct AnimationResult(bool Success, string? Detail = null)
{
    public static AnimationResult Ok() => new(true);
    public static AnimationResult Fail(string detail) => new(false, detail);
}

/// <summary>Stores animation state for the current session.</summary>
public sealed class AnimationSession
{
    private readonly IAnimationRuntimePort _port;
    private readonly Dictionary<ActorId, AnimationOverrides> _overrides = new();

    /// <summary>Whether the session owns the global physics freeze.</summary>
    private bool _sceneOwnsPhysics;

    public AnimationSession(IAnimationRuntimePort port)
    {
        _port = port;
    }

    public IReadOnlyCollection<ActorId> OwnedActors => _overrides.Keys;

    public AnimationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var value) ? value : AnimationOverrides.None;

    public ActorAnimationReading? Read(ActorId actor) => _port.Read(actor);

    /// <summary>True while animation commands are temporarily suspended.</summary>
    public bool CommandsSuspended { get; private set; }

    public void SuspendCommands()
    {
        CommandsSuspended = true;
        // Pause loop playback while commands are suspended.
        _port.LoopsSuspended = true;
    }

    public void ResumeCommands()
    {
        CommandsSuspended = false;
        _port.LoopsSuspended = false;
    }

    private AnimationResult? Suspended() => CommandsSuspended
        ? AnimationResult.Fail("A face capture is in progress.")
        : null;

    public bool IsSupported(ActorId actor) => _port.IsSupported(actor);

    public bool IsPhysicsFrozen => _port.IsPhysicsFrozen;

    private AnimationOverrides Mutate(ActorId actor, Func<AnimationOverrides, AnimationOverrides> change)
    {
        var updated = change(OverridesFor(actor));
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        return updated;
    }

    // ── Base and blend ────────────────────────────────────────────────

    /// <summary>Plays and persistently loops the idle timeline.</summary>
    public AnimationResult PlayBase(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked)
            return blocked;
        if (timeline != AnimationTimelines.Idle)
            return Blend(actor, timeline);
        if (!_port.SupportsForceLoop)
            return AnimationResult.Fail("Persistent animation looping is unavailable.");

        var result = Blend(actor, timeline);
        if (!result.Success)
            return result;

        var forced = _port.SetForceLoop(actor, timeline);
        if (!forced.Success)
            return AnimationResult.Fail(
                forced.Detail ?? "The animation could not be kept looping.");

        Mutate(actor, o => o with { BaseTimeline = timeline });
        return AnimationResult.Ok();
    }

    /// <summary>Plays a timeline and captures state for reset.</summary>
    public AnimationResult Blend(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);

        // Capture the incoming non-base slot before replacing it.
        AnimationSlot? landing = _port.TimelineSlot(timeline);
        bool captureSlot = landing is { } slot &&
            slot != AnimationSlot.Base &&
            !current.SlotCaptures.ContainsKey(slot);
        ushort incoming = 0;
        if (captureSlot && _port.Read(actor) is { } reading)
            incoming = reading.TimelineFor(landing!.Value);

        var result = _port.Blend(actor, timeline, current.BaseCapture, out var captured);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Blend failed.");
        if (captured is { } taken)
            Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        if (captureSlot)
        {
            var landed = landing!.Value;
            Mutate(actor, o =>
            {
                if (o.SlotCaptures.ContainsKey(landed))
                    return o;
                var slots = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures)
                {
                    [landed] = incoming,
                };
                return o with { SlotCaptures = slots };
            });
        }
        return AnimationResult.Ok();
    }

    public AnimationResult PlayEmote(ActorId actor, uint emoteId)
    {
        if (Suspended() is { } blocked) return blocked;
        // Emote playback also changes the base slot.
        var current = OverridesFor(actor);
        var captured = current.BaseCapture == null ? _port.CaptureBase(actor) : null;
        var result = _port.PlayEmote(actor, emoteId);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Emote failed.");
        if (captured is { } taken)
            Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        return AnimationResult.Ok();
    }

    /// <summary>Restores the captured base state and clears the selection.</summary>
    public AnimationResult StopBase(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        if (current.BaseCapture is not { } capture)
        {
            Mutate(actor, o => o with { BaseTimeline = null });
            return AnimationResult.Ok();
        }

        var result = _port.RestoreBase(actor, capture);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Base restore failed.");
        // Clear the restored capture.
        Mutate(actor, o => o with { BaseTimeline = null, BaseCapture = null });
        return AnimationResult.Ok();
    }

    /// <summary>Plays a catalog entry through its selected route.</summary>
    public AnimationResult PlayEntry(
        ActorId actor, TimelineEntry entry, bool asBase, bool playFromStart)
    {
        var timeline = (ushort)entry.TimelineId;
        if (asBase && timeline == AnimationTimelines.Idle)
            return PlayBase(actor, timeline);
        if (playFromStart && entry.CanPlayFromStart)
        {
            var result = PlayEmote(actor, entry.EmoteId);
            if (result.Success)
                return result;
        }
        return Blend(actor, timeline);
    }

    /// <summary>Arms or disarms looping for one animation slot.</summary>
    public AnimationResult SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        if (Suspended() is { } blocked) return blocked;
        if (on && timeline == 0)
            return AnimationResult.Fail("Nothing to loop on this layer.");
        var result = on
            ? _port.SetSlotLoop(actor, slot, timeline)
            : _port.ClearSlotLoop(actor, slot);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Loop failed.");
        Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
            if (on)
                loops[slot] = timeline;
            else
                loops.Remove(slot);
            return o with { LoopedSlots = loops };
        });
        return AnimationResult.Ok();
    }

    /// <summary>Whether persistent main-animation looping is available.</summary>
    public bool SupportsForceLoop => _port.SupportsForceLoop;

    /// <summary>Whether stance transitions are available.</summary>
    public bool SupportsStance => _port.SupportsStance;

    /// <summary>Writes the persistent main-animation loop value.</summary>
    public AnimationResult SetForceLoop(ActorId actor, ushort timeline)
    {
        if (timeline != AnimationTimelines.Idle)
            return AnimationResult.Fail("Only the standard idle can persist.");
        var result = _port.SetForceLoop(actor, timeline);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Loop failed.");
    }

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationResult SetSpeed(ActorId actor, float speed)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.SetOverallSpeed(actor, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Speed failed.");
        Mutate(actor, o => o with { OverallSpeed = speed });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSpeed(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.ClearOverallSpeed(actor);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Speed reset failed.");
        Mutate(actor, o => o with { OverallSpeed = null });
        return AnimationResult.Ok();
    }

    public bool IsPaused(ActorId actor) => OverridesFor(actor).IsPaused;

    public AnimationResult Pause(ActorId actor) => SetSpeed(actor, 0f);

    /// <summary>Resume stops the session's speed override.</summary>
    public AnimationResult Resume(ActorId actor) => ClearSpeed(actor);

    /// <summary>Replays a timeline and reports whether a pause was released.</summary>
    public AnimationResult Replay(ActorId actor, ushort timeline, out bool resumed)
    {
        resumed = false;
        if (Suspended() is { } blocked) return blocked;
        if (IsPaused(actor))
        {
            var released = ClearSpeed(actor);
            if (!released.Success)
                return released;
            resumed = true;
        }
        return Blend(actor, timeline);
    }

    /// <summary>Rewinds paused controls to their first frame.</summary>
    public AnimationResult RewindPausedControls(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.RewindPausedControls(actor);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Rewind failed.");
    }

    public AnimationResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.SetSlotSpeed(actor, slot, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds) { [slot] = speed };
            return o with { SlotSpeeds = speeds };
        });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSlotSpeed(ActorId actor, AnimationSlot slot)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.ClearSlotSpeed(actor, slot);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed reset failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds);
            speeds.Remove(slot);
            return o with { SlotSpeeds = speeds };
        });
        return AnimationResult.Ok();
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    /// <summary>Sets the lip override. None restores the captured timeline.</summary>
    public AnimationResult SetLips(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        ushort? capture = current.LipsCapture;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = reading.LipsOverride;

        bool clearing = timeline == 0;
        ushort target = clearing ? capture ?? 0 : timeline;

        var result = _port.SetLips(actor, target);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Lips failed.");

        Mutate(actor, o => o with
        {
            Lips = clearing ? null : timeline,
            LipsCapture = clearing ? null : (o.LipsCapture ?? capture),
        });
        return AnimationResult.Ok();
    }

    public AnimationResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        if (Suspended() is { } blocked) return blocked;
        var capture = OverridesFor(actor).StanceCaptureValue;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = new StanceCapture(reading.Stance, reading.Pose);

        // A stance change stops the active animation first.
        var owned = OverridesFor(actor);
        if (owned.LoopedSlots.Count > 0)
        {
            _port.ClearLoops(actor);
            Mutate(actor, o => o with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
            });
            owned = OverridesFor(actor);
        }
        if (owned.BaseCapture != null || owned.BaseTimeline != null)
        {
            var released = StopBase(actor);
            if (!released.Success)
                return released;
        }

        var result = _port.SetStance(actor, stance, pose);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Stance failed.");
        Mutate(actor, o => o with { StanceCaptureValue = o.StanceCaptureValue ?? capture });
        return AnimationResult.Ok();
    }

    public AnimationResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        if (Suspended() is { } blocked) return blocked;
        var capture = OverridesFor(actor).WeaponCapture;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = reading.WeaponDrawn;

        var result = _port.SetWeaponDrawn(actor, drawn);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Weapon state failed.");
        Mutate(actor, o => o with { WeaponCapture = o.WeaponCapture ?? capture });
        return AnimationResult.Ok();
    }

    public AnimationResult SetPositionLock(ActorId actor, bool locked)
    {
        var result = _port.SetPositionLock(actor, locked);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Position lock failed.");
        Mutate(actor, o => o with { PositionLock = locked });
        return AnimationResult.Ok();
    }

    // ── Physics ──────────────────────────────────────────────────────

    /// <summary>Sets the scene-wide physics freeze after the change succeeds.</summary>
    public AnimationResult SetScenePhysicsFrozen(bool frozen)
    {
        if (frozen == _sceneOwnsPhysics)
            return AnimationResult.Ok();

        if (frozen != _port.IsPhysicsFrozen)
        {
            var result = _port.SetPhysicsFrozen(frozen);
            if (!result.Success)
                return AnimationResult.Fail(
                    result.Detail ?? (frozen
                        ? "Physics freeze failed."
                        : "Physics release failed."));
        }

        _sceneOwnsPhysics = frozen;
        return AnimationResult.Ok();
    }

    /// <summary>True when the scene owns the physics freeze.</summary>
    public bool SceneOwnsPhysics => _sceneOwnsPhysics;

    // ── Scrubbing ─────────────────────────────────────────────────────

    /// <summary>State captured for one scrub drag.</summary>
    private sealed record ScrubGesture(
        ActorId Actor,
        ScrubControlId Control,
        float Duration,
        ulong Token,
        bool WasPaused);

    private ScrubGesture? _scrub;

    /// <summary>Finds the live control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _port.FindSlotControl(actor, slot, out _);

    /// <summary>Starts a scrub and captures its control mapping.</summary>
    public AnimationResult BeginScrub(ActorId actor, ScrubControlId control)
    {
        var controls = _port.EnumerateControls(actor, out var token);
        ScrubControlReading? target = null;
        foreach (var reading in controls)
            if (reading.Id == control)
                target = reading;
        if (target == null)
            return AnimationResult.Fail("That animation control is no longer present.");

        // A scrub cannot move to another actor.
        if (_scrub is { } existing && !existing.Actor.Equals(actor))
            EndScrub();

        bool wasPaused = IsPaused(actor);
        if (!wasPaused)
        {
            var freeze = SetSpeed(actor, 0f);
            if (!freeze.Success)
                return freeze;
        }

        _scrub = new ScrubGesture(actor, control, target.Duration, token, wasPaused);
        return AnimationResult.Ok();
    }

    /// <summary>Updates the current scrubbed frame.</summary>
    public AnimationResult UpdateScrub(ActorId actor, float time)
    {
        if (_scrub is not { } gesture)
            return AnimationResult.Fail("No scrub is active.");
        if (!gesture.Actor.Equals(actor))
            return AnimationResult.Fail(
                "The scrub in flight belongs to a different actor.");
        if (!float.IsFinite(time))
            return AnimationResult.Fail("Scrub time must be a finite number.");

        float clamped = Math.Clamp(time, 0f, gesture.Duration);
        var result = _port.SetControlTime(
            gesture.Actor, gesture.Control, clamped, gesture.Token);
        if (result.Success)
            return AnimationResult.Ok();

        _scrub = null;
        return AnimationResult.Fail(result.Detail ?? "Scrub cancelled.");
    }

    /// <summary>Ends the drag and leaves the actor paused on that frame.</summary>
    public void EndScrub()
    {
        _scrub = null;
    }

    // ── Expression hold ──────────────────────────────────────────────────

    /// <summary>Plays an expression and holds the facial layer at speed 0.</summary>
    public AnimationResult HoldExpression(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var played = Blend(actor, timeline);
        if (!played.Success)
            return played;
        var pinned = SetSlotSpeed(actor, AnimationSlot.Facial, 0f);
        if (!pinned.Success)
            return pinned;
        Mutate(actor, o => o with { HeldExpression = timeline });
        return AnimationResult.Ok();
    }

    /// <summary>Releases a held expression.</summary>
    public AnimationResult ReleaseExpression(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var unpin = ClearSlotSpeed(actor, AnimationSlot.Facial);
        var straight = Blend(actor, AnimationTimelines.StraightFace);
        var again = ClearSlotSpeed(actor, AnimationSlot.Facial);
        var idle = Blend(actor, AnimationTimelines.Idle);
        if (!unpin.Success || !straight.Success || !again.Success || !idle.Success)
        {
            // Keep the hold when release is incomplete so it can be retried.
            return AnimationResult.Fail(
                unpin.Detail ?? straight.Detail ?? again.Detail ?? idle.Detail ??
                "Expression release failed.");
        }
        Mutate(actor, o => o with { HeldExpression = null });
        return AnimationResult.Ok();
    }

    /// <summary>Restores the facial layer and consumes its capture.</summary>
    public AnimationResult RestoreFacialLayer(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        // The key records whether Poser changed this layer.
        bool played = OverridesFor(actor)
            .SlotCaptures.TryGetValue(AnimationSlot.Facial, out var captured);

        var unpin = ClearSlotSpeed(actor, AnimationSlot.Facial);
        if (!unpin.Success)
            return unpin;
        if (played)
        {
            var replayed = Blend(
                actor,
                captured != 0 ? captured : AnimationTimelines.StraightFace);
            if (!replayed.Success)
                // Keep the state while the layer still needs restoration.
                return replayed;
        }

        Mutate(actor, o =>
        {
            var slots = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
            slots.Remove(AnimationSlot.Facial);
            return o with { HeldExpression = null, SlotCaptures = slots };
        });
        return AnimationResult.Ok();
    }

    /// <summary>The expression currently held on the face, if any.</summary>
    public ushort? HeldExpressionFor(ActorId actor) =>
        OverridesFor(actor).HeldExpression;

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>Restores all overrides owned by one actor.</summary>
    public AnimationResult ResetActor(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        if (!_overrides.TryGetValue(actor, out var owned))
        {
            // Physics belongs to the scene, not to an actor.
            _port.ClearLoops(actor);
            return AnimationResult.Ok();
        }

        // Keep failed restores owned for the next reset.
        var failures = new List<string>();
        var remaining = owned;
        bool actorGone = !_port.IsSupported(actor) && _port.Read(actor) == null;

        bool Try(AnimationPortResult result)
        {
            if (result.Success)
                return true;
            if (result.Detail is { } detail)
                failures.Add(detail);
            return false;
        }

        // Clear loops before restoring the saved state.
        if (owned.LoopedSlots.Count > 0)
        {
            _port.ClearLoops(actor);
            remaining = remaining with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
            };
        }

        if (owned.OverallSpeed != null && Try(_port.ClearOverallSpeed(actor)))
            remaining = remaining with { OverallSpeed = null };

        // Release a held expression before clearing its speed override.
        if (owned.HeldExpression != null &&
            Try(_port.ClearSlotSpeed(actor, AnimationSlot.Facial)) &&
            Try(_port.Blend(actor, AnimationTimelines.StraightFace, remaining.BaseCapture, out _)) &&
            Try(_port.ClearSlotSpeed(actor, AnimationSlot.Facial)) &&
            Try(_port.Blend(actor, AnimationTimelines.Idle, remaining.BaseCapture, out _)))
        {
            remaining = remaining with { HeldExpression = null };
            if (remaining.SlotSpeeds.ContainsKey(AnimationSlot.Facial))
            {
                var speeds = new Dictionary<AnimationSlot, float>(remaining.SlotSpeeds);
                speeds.Remove(AnimationSlot.Facial);
                remaining = remaining with { SlotSpeeds = speeds };
            }
        }

        // Restore captured slot timelines after clearing empty slots.
        if (owned.SlotCaptures.Count > 0)
        {
            var liveRead = _port.Read(actor);
            bool cancelNeeded = owned.SlotCaptures.Any(entry =>
                entry.Value == 0 && liveRead?.TimelineFor(entry.Key) is > 0);

            var slots = new Dictionary<AnimationSlot, ushort>(remaining.SlotCaptures);
            bool cancelled = true;
            if (cancelNeeded)
            {
                if (liveRead != null)
                    foreach (var slotReading in liveRead.Slots)
                        if (slotReading.Slot != AnimationSlot.Base &&
                            slotReading.TimelineId != 0 &&
                            !slots.ContainsKey(slotReading.Slot))
                            slots[slotReading.Slot] = slotReading.TimelineId;
                cancelled = Try(_port.CancelActiveTimeline(actor));
            }

            // Do not replay slots when cancellation fails.
            if (cancelled)
            {
                foreach (var (slot, incoming) in slots.ToList())
                {
                    if (incoming == 0)
                        slots.Remove(slot);
                    else if (Try(_port.Blend(actor, incoming, remaining.BaseCapture, out _)))
                        slots.Remove(slot);
                }
            }
            remaining = remaining with { SlotCaptures = slots };
        }

        // Restore the base last.
        if (owned.BaseCapture is { } capture && Try(_port.RestoreBase(actor, capture)) &&
            remaining.HeldExpression == null && remaining.SlotCaptures.Count == 0)
        {
            remaining = remaining with { BaseCapture = null, BaseTimeline = null };
        }

        if (owned.SlotSpeeds.Count > 0)
        {
            var speeds = new Dictionary<AnimationSlot, float>(remaining.SlotSpeeds);
            foreach (var slot in owned.SlotSpeeds.Keys.ToList())
                if (Try(_port.ClearSlotSpeed(actor, slot)))
                    speeds.Remove(slot);
            remaining = remaining with { SlotSpeeds = speeds };
        }

        if (owned.StanceCaptureValue is { } stance &&
            Try(_port.SetStance(actor, stance.Stance, stance.Pose)))
            remaining = remaining with { StanceCaptureValue = null };
        if (owned.WeaponCapture is { } weapon &&
            Try(_port.SetWeaponDrawn(actor, weapon)))
            remaining = remaining with { WeaponCapture = null };
        if (owned.LipsCapture is { } lips && Try(_port.SetLips(actor, lips)))
            remaining = remaining with { LipsCapture = null, Lips = null };
        if (owned.PositionLock && Try(_port.SetPositionLock(actor, false)))
            remaining = remaining with { PositionLock = false };

        if (actorGone || !remaining.HasAny)
        {
            _overrides.Remove(actor);
        }
        else
        {
            _overrides[actor] = remaining;
        }

        return failures.Count == 0
            ? AnimationResult.Ok()
            : AnimationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>Restores every actor owned by the session.</summary>
    public AnimationResult ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _overrides.Keys.ToList())
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        // Release the scene-wide physics hold after actor resets.
        var scene = SetScenePhysicsFrozen(false);
        if (!scene.Success && scene.Detail is { } sceneDetail)
            failures.Add($"scene: {sceneDetail}");
        return failures.Count == 0
            ? AnimationResult.Ok()
            : AnimationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>Restores and removes state for actors no longer in the scene.</summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        // The scene-wide physics hold is handled separately.
        var departed = _overrides.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in departed)
        {
            // Remove the actor's state after attempting restoration.
            ResetActor(id);
        }
    }
}
