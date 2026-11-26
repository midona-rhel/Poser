using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>
/// Scrubbing: dragging an animation to a point in time. Remembers the drag in
/// progress (which control, which actor, whether it was already paused) and
/// moves the game's animation clock.
/// </summary>
public sealed unsafe class AnimationScrubRuntimePort
{
    private readonly IPluginLog _log;
    private readonly AnimationNativeState _native;
    private readonly TimelineSeekService _seek;
    private readonly AnimationSpeedRuntimePort _speed;
#if DEBUG
    /// <summary>Debug bridge: scrub Base and Upper Body through the clock-writing
    /// fallback even where the native seek could move them.</summary>
    public static bool ForceFallback;
    private bool SeekAllowed => !ForceFallback;
#else
    private const bool SeekAllowed = true;
#endif

    public AnimationScrubRuntimePort(
        IPluginLog log, AnimationNativeState native, TimelineSeekService seek, AnimationSpeedRuntimePort speed)
    {
        _log = log;
        _native = native;
        _seek = seek;
        _speed = speed;
    }

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token)
    {
        token = 0;
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return Array.Empty<ScrubControlReading>();
        var controls = AnimationNativeState.CollectControls(character, out token);
        // Layers without a fixed Havok control are scrubbed by their timeline clock.
        var all = new List<ScrubControlReading>(controls);
        foreach (var slot in TimelineScrubSlots)
            if (_seek.TimelineReading(character, slot) is { } reading)
                all.Add(reading);
        return all;
    }

    /// <summary>Layers for which the seek service can provide a timeline clock reading.
    /// Reading the timeline avoids assuming a fixed order for Havok controls,
    /// whose array can change after a seek.</summary>
    private static readonly AnimationSlot[] TimelineScrubSlots =
        [AnimationSlot.Base, AnimationSlot.UpperBody, AnimationSlot.Facial, AnimationSlot.Additive, AnimationSlot.Lips,
         AnimationSlot.Parts1, AnimationSlot.Parts2, AnimationSlot.Parts3, AnimationSlot.Parts4, AnimationSlot.Overlay];

    /// <summary>
    /// A slot's scrub reading: its timeline clock where the native seek can move it;
    /// otherwise (an integer-frame clock, a scheduler-driven actor) its own C009
    /// controls, found through the slot timeline's tree, whatever their array order.
    /// Only without the seek's layout are Base and Upper Body read by control index.
    /// </summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return null;
        if (SeekAllowed && _seek.TimelineReading(character, slot) is { } timeline)
            return timeline;
        if (Engine is { } engine)
            return SlotControlReading(engine, character, slot);
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return null;
        int index = (int)slot;
        if (character->Timeline.TimelineSequencer.TimelineIds[index] == 0)
            return null;
        token = AnimationNativeState.ControlToken(character);
        ScrubControlReading? found = null;
        AnimationNativeState.ForEachControl(character, (p, c, control) =>
        {
            var binding = control->hkaAnimationControl.Binding;
            if (found == null && c == index && binding.ptr != null && binding.ptr->Animation.ptr != null
                && binding.ptr->Animation.ptr->Duration > 0f)
                found = new ScrubControlReading(new ScrubControlId(p, index),
                    control->hkaAnimationControl.LocalTime, binding.ptr->Animation.ptr->Duration,
                    control->PlaybackSpeed);
        });
        return found;
    }

    private TimelineSeekEngine? Engine => _seek.Available ? _seek.Engine : null;

    /// <summary>The slot's main animation control: its longest C009 control (the body
    /// animation; a slot's tree also carries short clips).</summary>
    private static nint MainControl(List<nint> controls)
    {
        nint main = 0;
        float longest = 0f;
        foreach (nint control in controls)
            if (TimelineSeekEngine.ControlLength(control) is var length && length > longest)
                (main, longest) = (control, length);
        return main;
    }

    /// <summary>The slot's main control, as a reading of the slot.</summary>
    private static ScrubControlReading? SlotControlReading(TimelineSeekEngine engine, Character* character, AnimationSlot slot)
    {
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, (int)slot);
        if (root == 0 || !TimelineState.Of(root).Loaded)
            return null;
        nint control = MainControl(engine.SlotControls(root));
        return control == 0 ? null : new ScrubControlReading(ScrubControlId.Timeline(slot),
            *(float*)(control + NativeFields.hkaDefaultAnimationControl_LocalTime), TimelineSeekEngine.ControlLength(control), *(float*)(control + NativeFields.hkaDefaultAnimationControl_PlaybackSpeed));
    }

    /// <summary>
    /// Fallback for layers the native seek cannot move. Clamp the requested time
    /// one frame before the main control's end, set the layer's controls, move its
    /// props by the same time difference, and update its timeline clocks. Avoiding
    /// the main timeline's exact end prevents its end events from getting stuck.
    /// </summary>
    private static Outcome ScrubSlotFallback(TimelineSeekEngine engine, Character* character, AnimationSlot slot, float time)
    {
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, (int)slot);
        var controls = root != 0 ? engine.SlotControls(root) : new List<nint>();
        nint main = MainControl(controls);
        if (main == 0)
            return Outcome.Fail("This layer's animation can no longer be scrubbed.");
        float duration = TimelineSeekEngine.ControlLength(main);
        time = Math.Clamp(time, 0f, Math.Max(0f, duration - 1f / 30f));
        float before = *(float*)(main + NativeFields.hkaDefaultAnimationControl_LocalTime);
        foreach (nint control in controls)
            *(float*)(control + NativeFields.hkaDefaultAnimationControl_LocalTime) = Math.Clamp(time, 0f, Math.Max(0f, TimelineSeekEngine.ControlLength(control)));
        engine.ShiftPropControls((nint)character, root, time - before);
        engine.WriteClocks(root, time * 30f);
        return Outcome.Ok();
    }

    public Outcome SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (control.IsTimeline)
        {
            var slot = (AnimationSlot)control.Control;
            if (SeekAllowed && _seek.Supports(character, (int)slot))
            {
                _seek.RequestTimeline(actor, character, slot, time);
                return Outcome.Ok();
            }
            // A layer just played again loads first; its target waits for it.
            if (_seek.RequestTimelineWhenLoaded(actor, character, slot, time))
                return Outcome.Ok();
            return Engine is { } engine
                ? ScrubSlotFallback(engine, character, slot, time)
                : Outcome.Fail("This layer's animation can no longer be scrubbed.");
        }

        // A control by its index. Without the seek's layout: the Base or Upper Body
        // control, the same index on every partial (body, face, hair each run their
        // own), and the slot timeline's own clock.
        if (token != 0 && token != AnimationNativeState.ControlToken(character))
            return Outcome.Fail("Skeleton changed; scrub cancelled.");
        float duration = -1f;
        AnimationNativeState.ForEachControl(character, (p, c, target) =>
        {
            var binding = target->hkaAnimationControl.Binding;
            if (p == control.Partial && c == control.Control && binding.ptr != null && binding.ptr->Animation.ptr != null)
                duration = binding.ptr->Animation.ptr->Duration;
        });
        if (!(duration >= 0f))
            return Outcome.Fail("Scrub target no longer exists.");
        // A Base or Upper Body control picked from the control list: the native seek
        // moves its slot where it can, the slot fallback where it can't.
        if (control.Control is 0 or 1)
        {
            if (SeekAllowed && _seek.Supports(character, control.Control))
            {
                float from = 0f;
                AnimationNativeState.ForEachControl(character, (p, c, target) =>
                {
                    if (p == control.Partial && c == control.Control)
                        from = target->hkaAnimationControl.LocalTime;
                });
                _seek.Request(actor, character, (AnimationSlot)control.Control, time, from);
                return Outcome.Ok();
            }
            if (Engine is { } engine)
                return ScrubSlotFallback(engine, character, (AnimationSlot)control.Control, time);
        }
        time = Math.Clamp(time, 0f, Math.Max(0f, duration - 1f / 30f));
        AnimationNativeState.ForEachControl(character, (_, c, each) =>
        {
            if (c != control.Control)
                return;
            var binding = each->hkaAnimationControl.Binding;
            float clip = binding.ptr != null && binding.ptr->Animation.ptr != null ? binding.ptr->Animation.ptr->Duration : duration;
            each->hkaAnimationControl.LocalTime = Math.Clamp(time, 0f, clip);
        });
        if (AnimationNativeState.SchedulerTimestamp(&character->Timeline.TimelineSequencer, control.Control)
            is var timestamp && timestamp != null)
        {
            *timestamp = time * 30f;
            *(timestamp + 1) = time * 30f;
        }
        return Outcome.Ok();
    }

    // ── The drag in progress ──────────────────────────────────────────

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
        SetControlTime(actor, ScrubControlId.Timeline(slot), Math.Max(seconds, 0f), 0);

    /// <summary>Gets the control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        FindSlotControl(actor, slot, out _);

    /// <summary>
    /// Finds the animation control, pauses it, and remembers its duration
    /// and skeleton token. Returns an error if the control cannot be found.
    /// </summary>
    public Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null)
    {
        var controls = EnumerateControls(actor, out var token);
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
    public Outcome UpdateScrub(ActorId actor, float time, Guid owner)
    {
        if (_scrub is not { } gesture)
            return Outcome.Fail("No scrub is active.");
        if (gesture.Owner != owner)
            return Outcome.Fail("The scrub in flight belongs to another control.");
        if (!gesture.Actor.Equals(actor))
            return Outcome.Fail(
                "The scrub in flight belongs to a different actor.");

        float clamped = Math.Clamp(time, 0f, gesture.Duration);
        var result = SetControlTime(
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
