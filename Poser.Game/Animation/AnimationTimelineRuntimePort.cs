using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Entities;

namespace Poser.Game.Animation;

/// <summary>Native reads, timeline playback, loops and lips.</summary>
public sealed unsafe class AnimationTimelineRuntimePort : IAnimationTimelinePort, IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly AnimationNativeState _native;
    private readonly SceneSession _scene;
    private readonly AnimationSpeedRuntimePort? _speeds;

    public AnimationTimelineRuntimePort(
        IFramework framework,
        IPluginLog log,
        AnimationNativeState native,
        SceneSession scene,
        AnimationSpeedRuntimePort? speeds = null,
        TimelineSeekService? seek = null)
    {
        _seek = seek;
        if (_seek != null)
            _seek.LayerEnded += (actor, slot, duration) => LayerEnded?.Invoke(actor, slot, duration);
        _speeds = speeds;
        _framework = framework;
        _log = log;
        _native = native;
        _scene = scene;
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsSupported(ActorId actor)
    {
        if (_native.ResolveActor(actor) is not { } live)
            return false;
        return IsSupportedActor(live, _scene.Snapshot.FindActor(actor));
    }

    internal static bool IsSupportedActor(
        IActor live,
        Poser.Domain.Scene.ActorDescriptor? descriptor) =>
        live.CanControlAnimation
        || descriptor is
        {
            IsCompanion: true,
            OwnerActor: not null,
            AttachmentKind: not null,
        };

    // ── Reads ─────────────────────────────────────────────────────────

    public ActorAnimationReading? Read(ActorId actor)
    {
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return null;

        var slots = new List<AnimationSlotReading>(AnimationSlots.All.Count);
        foreach (var slot in AnimationSlots.All)
        {
            int index = (int)slot;
            slots.Add(new AnimationSlotReading(
                slot,
                character->Timeline.TimelineSequencer.TimelineIds[index],
                character->Timeline.TimelineSequencer.TimelineSpeeds[index]));
        }

        var controls = AnimationNativeState.CollectControls(character, out var token);
        // Sitting and sleeping are the game's posture (from the mode); the pose kind
        // (+0x20) is cleared with the pose, so it only tells the standing families apart.
        var stance = _native.Posture(character) switch
        {
            1 => AnimationStance.SitGround,
            2 => AnimationStance.SitChair,
            4 => AnimationStance.Sleeping,
            _ => character->EmoteController.CurrentPoseType switch
            {
                EmoteController.PoseType.WeaponDrawn => AnimationStance.WeaponDrawn,
                EmoteController.PoseType.Umbrella => AnimationStance.Umbrella,
                EmoteController.PoseType.Accessory => AnimationStance.Accessory,
                _ => AnimationStance.Idle,
            },
        };

        return new ActorAnimationReading(
            character->Timeline.BaseOverride,
            character->Timeline.OverallSpeed,
            character->Timeline.LipsOverride,
            character->Timeline.IsWeaponDrawn,
            stance,
            character->EmoteController.CPoseState,
            slots,
            controls,
            token);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        EnforceLoops(framework);
#if DEBUG
        _native.ProbeTick();
#endif
    }

    // ── Base, blend, loop ─────────────────────────────────────────────

    private readonly TimelineSeekService? _seek;

    /// <summary>A swap into a paused slot: its cross-fade would never advance at
    /// speed 0 and keep showing the old animation (old control at weight 1, new
    /// at 0). The new animation appears on its frame 0 at full weight instead.</summary>
    private void PrepareSwap(Character* character, ushort timeline)
    {
        if (_seek != null && _native.TimelineSlot(timeline) is { } slot)
            _seek.PrepareInstantSwap(character, (int)slot);
    }

    /// <summary>Plays a timeline through the native route.</summary>
    public Outcome Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        if (existing == null)
            captured = CaptureBase(character);

        PrepareSwap(character, timeline);
        return _native.PlayTimeline(character, timeline);
    }

    public Outcome PlayBase(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        if (existing == null)
            captured = CaptureBase(character);

        PrepareSwap(character, timeline);
        var played = _native.PlayTimeline(character, timeline);
        if (played.Success)
            _seek?.ClearLoop(actor, AnimationSlot.Base);
        return played;
    }

    /// <summary>The slot the sheet's Stance column routes a timeline
    /// onto, or null when the row is missing or unmapped.</summary>
    public AnimationSlot? TimelineSlot(ushort timeline) => _native.TimelineSlot(timeline);

    /// <summary>Cancels one animation layer, including its scheduler and animation control.</summary>
    public Outcome ClearSlotTimeline(ActorId actor, AnimationSlot slot)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        int index = (int)slot;
        if (index is < 0 or >= 14)
            return Outcome.Fail("Slot out of range.");
        if (!_native.HasCancelTimeline)
            return Outcome.Fail(
                "Timeline cancellation is unavailable: the game function was not found.");
        // CancelTimeline takes a slot index, not a timeline row id, as argument two.
        // Keep argument three zero to avoid resetting the base animation.
        _native.CancelTimeline(&character->Timeline, (nint)index, nint.Zero);
        return Outcome.Ok();
    }

    /// <summary>Captures the current base state.</summary>
    public BaseAnimationCapture? CaptureBase(ActorId actor)
    {
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return null;
        return CaptureBase(character);
    }

    private static BaseAnimationCapture CaptureBase(Character* character) => new(
        (byte)character->Mode,
        character->ModeParam,
        character->Timeline.BaseOverride,
        character->Timeline.TimelineSequencer.TimelineIds[0]);

    /// <summary>The Full body back to what the game played before Poser's first write:
    /// its mode and BaseOverride, the slot cleared for the game's own pick.</summary>
    public Outcome RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        return _native.RestoreFullBody(character, (CharacterModes)capture.Mode, capture.ModeParam,
            capture.BaseTimeline, capture.BaseSlotTimeline);
    }

    public ushort? EmotePostureRow(ActorId actor, uint emoteId)
    {
        var character = _native.Resolve(actor, out _);
        return character == null ? null : _native.EmotePostureRow(character, emoteId);
    }

    public Outcome PlayEmote(ActorId actor, uint emoteId, ushort loop)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (_native.PlayEmoteFromStart(character, emoteId, loop))
            return Outcome.Ok();
        _log.Information($"Animation: emote {emoteId} did not start from its intro for actor={actor}.");
        return Outcome.Fail("The emote could not start from its intro.");
    }

    // ── Loops ───────────────────────────────────────────

    /// <summary>One armed loop. The cooldown keeps the frame or two of
    /// play transition from re-firing the play every tick.</summary>
    private sealed class LoopArm
    {
        public ushort Timeline;
        public int Cooldown;
    }

    private const int LoopCooldownTicks = 15;
    private readonly Dictionary<ActorId, Dictionary<int, LoopArm>> _loops = new();

    public bool LoopsSuspended
    {
        get => _loopsSuspended;
        set
        {
            _loopsSuspended = value;
            if (_seek != null)
                _seek.LoopsSuspended = value;
        }
    }
    private bool _loopsSuspended;

    public Outcome SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!_loops.TryGetValue(actor, out var slots))
            _loops[actor] = slots = new Dictionary<int, LoopArm>();
        slots[(int)slot] = new LoopArm { Timeline = timeline, Cooldown = LoopCooldownTicks };
        // The seek service wraps the layer before it ends. EnforceLoops restarts it
        // if wrapping misses the end or the layer cannot be seeked.
        _seek?.SetLoop(actor, slot, timeline);
        return Outcome.Ok();
    }

    public Outcome ClearSlotLoop(ActorId actor, AnimationSlot slot)
    {
        _seek?.ClearLoop(actor, slot);
        if (_loops.TryGetValue(actor, out var slots))
        {
            slots.Remove((int)slot);
            if (slots.Count == 0)
                _loops.Remove(actor);
        }
        return Outcome.Ok();
    }

    public void SetLayerOwned(ActorId actor, AnimationSlot slot, bool owned) =>
        _seek?.SetOwned(actor, slot, owned);

    public event Action<ActorId, AnimationSlot, float>? LayerEnded;

    public void ClearLoops(ActorId actor)
    {
        _loops.Remove(actor);
        _seek?.ClearLoops(actor);
    }

    /// <summary>Restarts a loop when the slot no longer contains the requested timeline.</summary>
    private void EnforceLoops(IFramework framework)
    {
        if (LoopsSuspended || _loops.Count == 0)
            return;
        foreach (var (actor, slots) in _loops)
        {
            var character = _native.Resolve(actor, out _);
            if (character == null)
                continue;
            foreach (var (slot, arm) in slots)
            {
                if (arm.Cooldown > 0)
                {
                    arm.Cooldown--;
                    continue;
                }
                if (character->Timeline.TimelineSequencer.TimelineIds[slot] != arm.Timeline)
                {
                    var replayed = _native.PlayTimeline(character, arm.Timeline);
                    if (!replayed.Success)
                        _log.Warning(
                            $"Animation: slot loop replay failed actor={actor} slot={slot} " +
                            $"timeline={arm.Timeline}: {replayed.Detail}");
                    arm.Cooldown = LoopCooldownTicks;
                }
            }
        }
    }

    /// <summary>
    /// Full body repeat is the game's own: BaseOverride names the row, so the base-idle
    /// pick plays it again when it ends, plays it after a redraw and leaves it alone
    /// while it is held. 0 lets the slot go back to the game's base.
    /// </summary>
    public Outcome SetForceLoop(ActorId actor, ushort timeline)
    {
        if (timeline != 0 && TimelineSlot(timeline) != AnimationSlot.Base)
            return Outcome.Fail("Only full-body timelines can use repeat.");
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        character->Timeline.BaseOverride = timeline;
        if (timeline == 0)
            _seek?.ClearLoop(actor, AnimationSlot.Base);
        else
            _seek?.SetLoop(actor, AnimationSlot.Base, timeline);
        return Outcome.Ok();
    }


    // ── Lips ──────────────────────────────────────────────────────────

    public Outcome SetLips(ActorId actor, ushort timeline)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        // Must go through the native setter: it does sequencer bookkeeping
        // that a direct field write skips.
        character->Timeline.SetLipsOverrideTimeline(timeline);
        return Outcome.Ok();
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _loops.Clear();
#if DEBUG
        _native.DisposeProbeHooks();
#endif
        GC.SuppressFinalize(this);
    }
}
