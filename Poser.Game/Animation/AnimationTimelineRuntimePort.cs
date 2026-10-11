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
    // Poser-owned forced timelines are reasserted if a native animation
    // update clears the field while repeat remains armed.
    private readonly Dictionary<ActorId, ushort> _forcedLoops = new();

    public AnimationTimelineRuntimePort(
        IFramework framework,
        IPluginLog log,
        AnimationNativeState native,
        SceneSession scene)
    {
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
        // Preserve the native pose family so each stance stays selectable.
        var poseType = character->EmoteController.CurrentPoseType;
        var stance = poseType switch
        {
            EmoteController.PoseType.WeaponDrawn => AnimationStance.WeaponDrawn,
            EmoteController.PoseType.Sit => AnimationStance.SitChair,
            EmoteController.PoseType.GroundSit => AnimationStance.SitGround,
            EmoteController.PoseType.Doze => AnimationStance.Sleeping,
            EmoteController.PoseType.Umbrella => AnimationStance.Umbrella,
            EmoteController.PoseType.Accessory => AnimationStance.Accessory,
            _ => AnimationStance.Idle,
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
        EnforceForcedLoops();
        EnforceLoops(framework);
#if DEBUG
        _native.ProbeTick();
#endif
    }

    // ── Base, blend, loop ─────────────────────────────────────────────

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

        var played = _native.PlayTimeline(character, timeline);
        if (played.Success)
            _forcedLoops.Remove(actor);
        return played;
    }

    /// <summary>The slot the sheet's Stance column routes a timeline
    /// onto, or null when the row is missing or unmapped.</summary>
    public AnimationSlot? TimelineSlot(ushort timeline) => _native.TimelineSlot(timeline);

    /// <summary>Clears one layered slot for real: the game's CancelTimeline
    /// takes the SLOT as its second argument and drops that layer's
    /// scheduler and control. A container-level cancel (slot 0) left the
    /// layer alive and the base restore re-scheduled it (reset restarted
    /// breakfast from 0, 2026-09-01 22:4x).</summary>
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
        // CancelTimeline's second argument IS the slot (bridge experiment
        // 22:41: a2=1 dropped the upper layer, its scheduler and its havok
        // control cleanly; a2=timeline id did nothing; a3=1 reset the base).
        _native.CancelTimeline(&character->Timeline, (nint)index, nint.Zero);
        return Outcome.Ok();
    }

    /// <summary>Cancels the active container timeline.</summary>
    public Outcome CancelActiveTimeline(ActorId actor)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!_native.HasCancelTimeline)
            return Outcome.Fail(
                "Timeline cancellation is unavailable: the game function was not found.");
        _native.CancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
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
        AnimationNativeState.ReadModeParam(character),
        character->Timeline.BaseOverride,
        character->Timeline.TimelineSequencer.TimelineIds[0],
        AnimationNativeState.TryReadForcedTimeline(&character->Timeline, out var forced)
            ? forced : (ushort)0);

    public Outcome RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        AnimationNativeState.WriteModeParam(character, capture.ModeParam);
        var played = _native.PlayTimeline(
            character,
            capture.BaseSlotTimeline != 0
                ? capture.BaseSlotTimeline
                : AnimationTimelines.Idle);
        if (!played.Success)
            return played;
        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        AnimationNativeState.WriteModeParam(character, capture.ModeParam);
        AnimationNativeState.TrySetForcedTimeline(&character->Timeline, capture.ForcedTimeline);
        return Outcome.Ok();
    }

    public Outcome PlayEmote(ActorId actor, uint emoteId)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        AnimationNativeState.TrySetForcedTimeline(&character->Timeline, 0);
        return _native.PlayEmoteNative(character, emoteId)
            ? Outcome.Ok()
            : Outcome.Fail("The emote entry point is unavailable.");
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

    public bool LoopsSuspended { get; set; }

    public Outcome SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!_loops.TryGetValue(actor, out var slots))
            _loops[actor] = slots = new Dictionary<int, LoopArm>();
        slots[(int)slot] = new LoopArm { Timeline = timeline, Cooldown = LoopCooldownTicks };
        return Outcome.Ok();
    }

    public Outcome ClearSlotLoop(ActorId actor, AnimationSlot slot)
    {
        if (_loops.TryGetValue(actor, out var slots))
        {
            slots.Remove((int)slot);
            if (slots.Count == 0)
                _loops.Remove(actor);
        }
        return Outcome.Ok();
    }

    public void ClearLoops(ActorId actor)
    {
        _loops.Remove(actor);
        _forcedLoops.Remove(actor);
    }

    /// <summary>
    /// Replays an owned Base timeline when the native forced field clears.
    /// The clear is the sequencer's lifecycle signal; rewriting the field
    /// alone after that point does not restart an animation that has ended.
    /// SetTimelineId routes the Base-tagged row without replacing other slots.
    /// </summary>
    private void EnforceForcedLoops()
    {
        if (LoopsSuspended || _forcedLoops.Count == 0)
            return;
        foreach (var (actor, timeline) in _forcedLoops)
        {
            var character = _native.Resolve(actor, out _);
            if (character == null ||
                !AnimationNativeState.TryReadForcedTimeline(&character->Timeline, out var current) ||
                current == timeline)
                continue;
            var replayed = _native.PlayTimeline(character, timeline);
            if (replayed.Success && AnimationNativeState.TrySetForcedTimeline(&character->Timeline, timeline))
                _log.Information(
                    $"Animation: replayed full-body loop actor={actor} timeline={timeline} field={timeline}.");
            else
                _log.Warning(
                    $"Animation: full-body loop replay failed actor={actor} timeline={timeline}: " +
                    (replayed.Detail ?? "forced field write failed."));
        }
    }

    /// <summary>Replays an owned slot after its native timeline drifts.</summary>
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
                    bool baseRearmed = !_forcedLoops.TryGetValue(actor, out var baseTimeline) ||
                        AnimationNativeState.TrySetForcedTimeline(&character->Timeline, baseTimeline);
                    if (!replayed.Success || !baseRearmed)
                    {
                        _log.Warning(
                            $"Animation: slot loop replay failed actor={actor} slot={slot} " +
                            $"timeline={arm.Timeline}: " +
                            (replayed.Detail ?? "full-body repeat rearm failed."));
                    }
                    arm.Cooldown = LoopCooldownTicks;
                }
            }
        }
    }

    public Outcome SetForceLoop(ActorId actor, ushort timeline)
    {
        if (!SupportsForceLoop)
            return Outcome.Fail("Full-body repeat is unavailable for this client layout.");
        if (timeline != 0 && TimelineSlot(timeline) != AnimationSlot.Base)
            return Outcome.Fail("Only full-body timelines can use repeat.");
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!AnimationNativeState.TrySetForcedTimeline(&character->Timeline, timeline) ||
            !AnimationNativeState.TryReadForcedTimeline(&character->Timeline, out var written) ||
            written != timeline)
            return Outcome.Fail("The full-body repeat field rejected the write.");
        if (timeline == 0)
            _forcedLoops.Remove(actor);
        else
            _forcedLoops[actor] = timeline;
        _log.Information(
            $"Animation: full-body loop actor={actor} timeline={timeline} field={written}.");
        return Outcome.Ok();
    }

    public bool SupportsForceLoop => AnimationNativeState.HasForcedTimelineLayout;

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
        _forcedLoops.Clear();
#if DEBUG
        _native.DisposeProbeHooks();
#endif
        GC.SuppressFinalize(this);
    }
}
