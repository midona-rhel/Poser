using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Animation;

/// <summary>Provides native animation operations.</summary>
public sealed unsafe partial class AnimationRuntimePort : IAnimationRuntimePort, IDisposable
{
    private readonly IFramework _framework;
    // Kept for the probe harness (AnimationRuntimePort.Probe.cs), which
    // arms its own hook lazily.
    private readonly ISigScanner _sigScanner;
    private readonly Dalamud.Plugin.Services.IGameInteropProvider _hooking;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _posing;
    // Authoritative, stable-id keyed.
    private readonly Dictionary<ActorId, Enforcement> _enforcement = new();
    // Poser-owned forced timelines are reasserted if a native animation
    // update clears the field while repeat remains armed.
    private readonly Dictionary<ActorId, ushort> _forcedLoops = new();
    // Derived index for the detours only; never a source of truth.
    private readonly Dictionary<nint, Enforcement> _byAddress = new();
    // Actors whose position lock this session created, so releasing it
    // cannot wipe a placement the user made with the gizmo.
    private readonly HashSet<ActorId> _positionLocks = new();

    private sealed class Enforcement
    {
        public float? OverallSpeed;
        public readonly Dictionary<int, float> SlotSpeeds = new();
        public bool IsEmpty => OverallSpeed == null && SlotSpeeds.Count == 0;
    }

    private delegate bool CalculateAndApplyOverallSpeedDelegate(TimelineContainer* container);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _speedHook;

    private delegate void SetSlotSpeedDelegate(ActionTimelineSequencer* sequencer, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _slotSpeedHook;

    // A hook object can exist after Enable fails. Commands gate on flags set
    // only after the matching hook is enabled.
    private readonly bool _overallSpeedHookEnabled;
    private readonly bool _slotSpeedHookEnabled;

    // These native entry points are resolved by signature.
    private delegate bool SetEmoteModeDelegate(EmoteController* controller, uint mode);
    private readonly SetEmoteModeDelegate? _setEmoteMode;
    private delegate nint CancelTimelineDelegate(TimelineContainer* container, nint a2, nint a3);
    private readonly CancelTimelineDelegate? _cancelTimeline;
    private delegate bool SetTimelineIdDelegate(
        ActionTimelineSequencer* timeline, ushort id, nint context);
    private readonly SetTimelineIdDelegate? _setTimelineId;
    // This native entry point takes four arguments.
    private delegate bool PlayEmoteDelegate(
        EmoteController* controller, nint emoteId, nint option, nint chair);
    private readonly PlayEmoteDelegate? _playEmote;

    /// <summary>Emote-mode argument values.</summary>
    private const uint EmoteModeNormal = 0;
    private const uint EmoteModeSitGround = 1;
    private const uint EmoteModeSitChair = 2;
    private const uint EmoteModeSleeping = 3;

    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTimeline>? _timelineSheet;
    // The scheduler layer (Ktisis Structs/Animation, offsets cross-checked
    // against our verified sequencer layout): per-slot SchedulerTimeline
    // HANDLES at sequencer+0x70 (Handle = { Data*, Flags }; Flags==0 means
    // dead), and the scheduler's own clock — TimelineController
    // .CurrentTimestamp — at +0x34 of the pointed object. This is the
    // second clock a real scrub must move: the havok controls are only the
    // sampling side, and the scheduler resets the animation on ITS time.
    private const int SequencerSchedulerHandlesOffset = 0x70;
    private const int SchedulerTimestampOffset = 0x34;

    /// <summary>The slot's live scheduler clock, or null.</summary>
    private static float* SchedulerTimestamp(
        ActionTimelineSequencer* sequencer, int slot)
    {
        if (slot is < 0 or >= 14)
            return null;
        var handles = (ulong*)((byte*)sequencer + SequencerSchedulerHandlesOffset);
        var handle = (SchedulerTimelineHandle*)handles[slot];
        if (handle == null || handle->Flags == 0 || handle->Data == 0)
            return null;
        return (float*)((byte*)handle->Data + SchedulerTimestampOffset);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedulerTimelineHandle
    {
        public nint Data;
        public uint Flags;
    }

    // Verified client layout: the forced id is container+0x2E0, which is
    // sequencer+0x2D0. SetTimelineId clears it, so layer writes clear first
    // and Base replay rearms it only after that native call returns.
    private const int ForcedTimelineOffset = 0x2E0;
    private const int SequencerForcedTimelineOffset = 0x2D0;
    private const int ForcedTimelineSize = sizeof(ushort);
    private static readonly int ModeParamOffset = (int)Marshal.OffsetOf<Character>(
        nameof(Character.ModeParam));
    private static readonly int TimelineSequencerOffset = (int)Marshal.OffsetOf<TimelineContainer>(
        nameof(TimelineContainer.TimelineSequencer));
    private static readonly int TimelineContainerSize = sizeof(TimelineContainer);
    private static readonly bool HasForcedTimelineLayout =
        HasForcedTimelineLayoutFor(TimelineSequencerOffset, TimelineContainerSize);

    // The physics freeze is a process-global code patch, not a per-actor
    // enforcement; the patcher owns its site, capability state and restore.
    private readonly PhysicsFreezePatcher _physics;

    public AnimationRuntimePort(
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        PosingService posing,
        IDataManager data)
    {
        _framework = framework;
        _sigScanner = sigScanner;
        _hooking = hooking;
        _timelineSheet = data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
        _log = log;
        _bindings = bindings;
        _posing = posing;
        _framework.Update += OnFrameworkUpdate;

        // A missing stance native degrades that one operation to an explicit
        // failure; it never silently half-applies a transition.
        _setEmoteMode = ScanDelegate<SetEmoteModeDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? F6 46 10 01", "SetEmoteMode");
        _cancelTimeline = ScanDelegate<CancelTimelineDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? 80 7B 17 01", "CancelTimeline");
        _setTimelineId = ScanDelegate<SetTimelineIdDelegate>(
            sigScanner,
            "E8 ?? ?? ?? ?? 4C 8B BC 24 ?? ?? ?? ?? 4C 8D 9C 24 ?? ?? ?? ?? 49 8B 5B 40",
            "SetTimelineId");
        _playEmote = ScanDelegate<PlayEmoteDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? 88 45 68", "PlayEmote");

        // Each hook's capability flag is set only after Enable succeeds.
        try
        {
            var speedAddress = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _speedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(
                speedAddress, OverallSpeedDetour);
            _speedHook.Enable();
            _overallSpeedHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Overall-speed hook unavailable; overall speed overrides will fail explicitly: {ex.Message}");
        }

        try
        {
            _slotSpeedHook = hooking.HookFromAddress<SetSlotSpeedDelegate>(
                ActionTimelineSequencer.Addresses.SetSlotSpeed.Value, SlotSpeedDetour);
            _slotSpeedHook.Enable();
            _slotSpeedHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Slot-speed hook unavailable; layer speed overrides will fail explicitly: {ex.Message}");
        }

        // Scan, byte validation, capability state and restore all live in
        // the patcher; an unavailable site degrades SetPhysicsFrozen to an
        // explicit failure with the patcher's own detail.
        _physics = new PhysicsFreezePatcher(sigScanner, log);
    }

    private T? ScanDelegate<T>(ISigScanner scanner, string signature, string name)
        where T : Delegate
    {
        try
        {
            if (scanner.TryScanText(signature, out var address) && address != nint.Zero)
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            _log.Warning($"Animation: {name} signature not found; stance changes will fail explicitly.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Animation: {name} scan failed ({ex.Message}); stance changes will fail explicitly.");
        }
        return null;
    }

    // ── Resolution ────────────────────────────────────────────────────

    private Character* Resolve(ActorId actor, out string? detail)
    {
        detail = null;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            detail = "Animation writes must run on the framework thread.";
            return null;
        }
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? $"Actor {actor} is no longer available.";
            return null;
        }
        var character = (Character*)legacy.Address;
        return character == null ? null : character;
    }

    private IActor? ResolveActor(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success ? resolved.Value : null;
    }

    public bool IsSupported(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return false;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success && resolved.Value is { CanControlAnimation: true };
    }

    // ── Enforcement index ─────────────────────────────────────────────

    private Enforcement EnforcementFor(ActorId actor)
    {
        if (!_enforcement.TryGetValue(actor, out var value))
            _enforcement[actor] = value = new Enforcement();
        return value;
    }

    private void PruneEnforcement(ActorId actor)
    {
        if (_enforcement.TryGetValue(actor, out var value) && value.IsEmpty)
            _enforcement.Remove(actor);
        SyncEnforcementIndex();
    }

    /// <summary>
    /// Rebuilds the detour-facing address index from the stable-id table.
    /// Must run on the framework thread; called after every override
    /// change and once per structural scene change, which is what keeps a
    /// redrawn actor from inheriting the previous body's enforcement.
    /// </summary>
    public void SyncEnforcementIndex()
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return;
        _byAddress.Clear();
        foreach (var (id, enforcement) in _enforcement)
        {
            var resolved = _bindings.Resolve(id);
            if (resolved.Success && resolved.Value is { } legacy && legacy.Address != nint.Zero)
                _byAddress[legacy.Address] = enforcement;
        }
    }

    private bool OverallSpeedDetour(TimelineContainer* container)
    {
        bool result = _speedHook!.Original(container);
        if (container == null)
            return result;
        var owner = (nint)container->OwnerObject;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var enforcement))
        {
            if (enforcement.OverallSpeed is { } speed)
            {
                // Run after the game's calculation so the override wins
                // whatever the game just decided.
                container->OverallSpeed = speed;
                result = true;
            }
            // The sampler's verdict (2026-09-01 19:37): writing the
            // slot-speed FIELD does not reliably reach the slot's havok
            // control — on the observed click frame the control kept x1
            // with the field at 0, and replays recreate controls at x1
            // regardless. A slot speed is therefore enforced on the
            // CONTROLS, every frame, here after the game's own update.
            if (enforcement.SlotSpeeds.Count > 0)
            {
                // Scaled by the container's overall: the game implements
                // the whole-actor pause by propagating overall × slot down
                // to the controls, and writing the raw slot value here
                // overrode that zero every frame — "pause doesn't do
                // anything once I've set it on an individual level".
                ApplySlotSpeedsToControls(
                    (Character*)owner,
                    enforcement.SlotSpeeds,
                    container->OverallSpeed);
                result = true;
            }
        }
        ProbeSeamPass(container);
        return result;
    }

    /// <summary>Writes each enforced slot speed onto that slot's live
    /// havok controls (control index == slot index on every partial).
    /// The per-frame half the field write cannot provide.</summary>
    private static void ApplySlotSpeedsToControls(
        Character* character, Dictionary<int, float> slotSpeeds, float overall)
    {
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;
        var skeleton = charaBase->Skeleton;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            foreach (var (slot, speed) in slotSpeeds)
            {
                if (slot >= animated->AnimationControls.Length)
                    continue;
                var control = animated->AnimationControls[slot].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed * overall;
            }
        }
    }

    private void SlotSpeedDetour(ActionTimelineSequencer* sequencer, uint slot, float speed)
    {
        float finalSpeed = speed;
        var owner = (nint)sequencer->Parent;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var enforcement) &&
            enforcement.SlotSpeeds.TryGetValue((int)slot, out var overrideSpeed))
        {
            finalSpeed = overrideSpeed;
        }
        _slotSpeedHook!.Original(sequencer, slot, finalSpeed);
    }

    // ── Reads ─────────────────────────────────────────────────────────

    public ActorAnimationReading? Read(ActorId actor)
    {
        var character = Resolve(actor, out _);
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

        var controls = CollectControls(character, out var token);
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
        ProbeTick();
    }

    /// <summary>Collects live animation controls.</summary>
    private static List<ScrubControlReading> CollectControls(Character* character, out ulong token)
    {
        token = 0;
        var result = new List<ScrubControlReading>();
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return result;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return result;
        var skeleton = charaBase->Skeleton;

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                result.Add(new ScrubControlReading(
                    new ScrubControlId(p, c),
                    control->hkaAnimationControl.LocalTime,
                    binding.ptr->Animation.ptr->Duration,
                    control->PlaybackSpeed));
            }
        }

        // The token identifies this skeleton and control layout. A redraw
        // moves the skeleton and changes the count, so a scrub captured
        // under the old token is refused rather than written blind.
        token = unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)result.Count);
        return result;
    }

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        return character == null
            ? Array.Empty<ScrubControlReading>()
            : CollectControls(character, out token);
    }

    /// <summary>Resolves the verified Base and Upper Body slot controls.</summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        if (character == null || slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return null;
        int index = (int)slot;
        if (character->Timeline.TimelineSequencer.TimelineIds[index] == 0)
            return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return null;
        var skeleton = charaBase->Skeleton;
        token = CurrentToken(skeleton);
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null || index >= animated->AnimationControls.Length)
                continue;
            var control = animated->AnimationControls[index].Value;
            if (control == null)
                continue;
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null ||
                binding.ptr->Animation.ptr->Duration <= 0f)
                continue;
            return new ScrubControlReading(
                new ScrubControlId(p, index),
                control->hkaAnimationControl.LocalTime,
                binding.ptr->Animation.ptr->Duration,
                control->PlaybackSpeed);
        }
        return null;
    }

    // ── Base, blend, loop ─────────────────────────────────────────────

    /// <summary>Plays a timeline through the native route.</summary>
    public AnimationPortResult Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        if (existing == null)
            captured = CaptureBase(character);

        return PlayTimeline(character, timeline);
    }

    public AnimationPortResult PlayBase(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        if (existing == null)
            captured = CaptureBase(character);

        var played = PlayTimeline(character, timeline);
        if (played.Success)
            _forcedLoops.Remove(actor);
        return played;
    }

    /// <summary>The slot the sheet's Stance column routes a timeline
    /// onto, or null when the row is missing or unmapped.</summary>
    public AnimationSlot? TimelineSlot(ushort timeline)
    {
        var stance = _timelineSheet?.GetRowOrDefault(timeline)?.Stance;
        return stance is { } value && AnimationSlots.IsKnown(value)
            ? (AnimationSlot)value
            : null;
    }

    /// <summary>Cancels the active container timeline.</summary>
    public AnimationPortResult CancelActiveTimeline(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (_cancelTimeline == null)
            return AnimationPortResult.Fail(
                "Timeline cancellation is unavailable: the game function was not found.");
        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        return AnimationPortResult.Ok();
    }

    /// <summary>Captures the current base state.</summary>
    public BaseAnimationCapture? CaptureBase(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        return CaptureBase(character);
    }

    private static BaseAnimationCapture CaptureBase(Character* character) => new(
        (byte)character->Mode,
        ReadModeParam(character),
        character->Timeline.BaseOverride,
        character->Timeline.TimelineSequencer.TimelineIds[0],
        TryReadForcedTimeline(&character->Timeline, out var forced) ? forced : (ushort)0);

    // ModeParam is a four-byte native field.
    private static uint ReadModeParam(Character* character) =>
        *(uint*)((byte*)character + ModeParamOffset);

    private static void WriteModeParam(Character* character, uint value) =>
        *(uint*)((byte*)character + ModeParamOffset) = value;

    private AnimationPortResult PlayTimeline(Character* character, ushort timeline)
    {
        if (_setTimelineId == null)
            return AnimationPortResult.Fail("Timeline playback is unavailable.");

        var mode = character->Mode;
        uint modeParam = ReadModeParam(character);
        ushort baseOverride = character->Timeline.BaseOverride;
        bool hadForced = TryReadForcedTimeline(&character->Timeline, out var forced);
        TrySetForcedTimeline(&character->Timeline, 0);
        if (PlayWithMode(character, timeline))
            return AnimationPortResult.Ok();

        character->Mode = mode;
        WriteModeParam(character, modeParam);
        character->Timeline.BaseOverride = baseOverride;
        if (hadForced)
            TrySetForcedTimeline(&character->Timeline, forced);
        return AnimationPortResult.Fail("Timeline playback failed.");
    }

    /// <summary>Applies the timeline mode before native playback.</summary>
    private bool PlayWithMode(Character* character, ushort timeline)
    {
        bool pause = _timelineSheet?.GetRowOrDefault(timeline)?.Pause ?? false;
        if (pause)
        {
            character->Mode = CharacterModes.EmoteLoop;
            character->ModeParam = 0;
        }
        else if (character->Mode == CharacterModes.EmoteLoop && character->ModeParam == 0)
        {
            character->Mode = CharacterModes.Normal;
        }
        else if (character->Mode == CharacterModes.AnimLock)
        {
            // Clear the active animation lock.
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;
        }
        _probeOurWrite = true;
        try
        {
            return _setTimelineId!(
                &character->Timeline.TimelineSequencer, timeline, nint.Zero);
        }
        finally
        {
            _probeOurWrite = false;
        }
    }

    public AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        WriteModeParam(character, capture.ModeParam);
        var played = PlayTimeline(
            character,
            capture.BaseSlotTimeline != 0
                ? capture.BaseSlotTimeline
                : AnimationTimelines.Idle);
        if (!played.Success)
            return played;
        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        WriteModeParam(character, capture.ModeParam);
        TrySetForcedTimeline(&character->Timeline, capture.ForcedTimeline);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult PlayEmote(ActorId actor, uint emoteId)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        TrySetForcedTimeline(&character->Timeline, 0);
        return PlayEmoteNative(character, emoteId)
            ? AnimationPortResult.Ok()
            : AnimationPortResult.Fail("The emote entry point is unavailable.");
    }

    /// <summary>Plays an emote through the game entry point.</summary>
    private bool PlayEmoteNative(Character* character, uint emoteId)
    {
        if (_playEmote == null)
            return false;
        _playEmote(&character->EmoteController, (nint)emoteId, nint.Zero, nint.Zero);
        return true;
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

    public AnimationPortResult SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!_loops.TryGetValue(actor, out var slots))
            _loops[actor] = slots = new Dictionary<int, LoopArm>();
        slots[(int)slot] = new LoopArm { Timeline = timeline, Cooldown = LoopCooldownTicks };
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearSlotLoop(ActorId actor, AnimationSlot slot)
    {
        if (_loops.TryGetValue(actor, out var slots))
        {
            slots.Remove((int)slot);
            if (slots.Count == 0)
                _loops.Remove(actor);
        }
        return AnimationPortResult.Ok();
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
            var character = Resolve(actor, out _);
            if (character == null ||
                !TryReadForcedTimeline(&character->Timeline, out var current) ||
                current == timeline)
                continue;
            var replayed = PlayTimeline(character, timeline);
            if (replayed.Success && TrySetForcedTimeline(&character->Timeline, timeline))
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
            var character = Resolve(actor, out _);
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
                    var replayed = PlayTimeline(character, arm.Timeline);
                    bool baseRearmed = !_forcedLoops.TryGetValue(actor, out var baseTimeline) ||
                        TrySetForcedTimeline(&character->Timeline, baseTimeline);
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

    public AnimationPortResult SetForceLoop(ActorId actor, ushort timeline)
    {
        if (!SupportsForceLoop)
            return AnimationPortResult.Fail("Full-body repeat is unavailable for this client layout.");
        if (timeline != 0 && TimelineSlot(timeline) != AnimationSlot.Base)
            return AnimationPortResult.Fail("Only full-body timelines can use repeat.");
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!TrySetForcedTimeline(&character->Timeline, timeline) ||
            !TryReadForcedTimeline(&character->Timeline, out var written) ||
            written != timeline)
            return AnimationPortResult.Fail("The full-body repeat field rejected the write.");
        if (timeline == 0)
            _forcedLoops.Remove(actor);
        else
            _forcedLoops[actor] = timeline;
        _log.Information(
            $"Animation: full-body loop actor={actor} timeline={timeline} field={written}.");
        return AnimationPortResult.Ok();
    }

    public bool SupportsForceLoop => HasForcedTimelineLayout;

    private static bool HasForcedTimelineLayoutFor(
        int timelineSequencerOffset, int timelineContainerSize) =>
        timelineSequencerOffset == 0x10 &&
        ForcedTimelineOffset == timelineSequencerOffset + SequencerForcedTimelineOffset &&
        timelineContainerSize >= timelineSequencerOffset +
            SequencerForcedTimelineOffset + ForcedTimelineSize;

    private static bool TryReadForcedTimeline(TimelineContainer* container, out ushort timeline)
    {
        if (!HasForcedTimelineLayout)
        {
            timeline = 0;
            return false;
        }
        timeline = *(ushort*)((byte*)container + ForcedTimelineOffset);
        return true;
    }

    private static bool TrySetForcedTimeline(TimelineContainer* container, ushort timeline)
    {
        return TrySetForcedTimelineForLayout(
            (nint)container,
            timeline,
            TimelineSequencerOffset,
            TimelineContainerSize);
    }

    private static bool TrySetForcedTimelineForLayout(
        nint container,
        ushort timeline,
        int timelineSequencerOffset,
        int timelineContainerSize)
    {
        if (!HasForcedTimelineLayoutFor(timelineSequencerOffset, timelineContainerSize))
            return false;
        *(ushort*)((byte*)container + ForcedTimelineOffset) = timeline;
        return true;
    }

    public bool SupportsStance => _setEmoteMode != null && _cancelTimeline != null;

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationPortResult SetOverallSpeed(ActorId actor, float speed)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(speed))
            return AnimationPortResult.Fail("Speed must be a finite number.");
        // Without the enabled hook, the game replaces the value next frame.
        if (!_overallSpeedHookEnabled)
            return AnimationPortResult.Fail(
                "Speed is unavailable: the game's speed hook is not active.");

        EnforcementFor(actor).OverallSpeed = speed;
        SyncEnforcementIndex();
        ApplySpeedNow(character, speed);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearOverallSpeed(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        // Resolve ownership before dropping enforcement. The hand-back write
        // runs only for a speed Poser enforced.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            enforcement.OverallSpeed == null)
            return AnimationPortResult.Ok();

        enforcement.OverallSpeed = null;
        PruneEnforcement(actor);
        // Hand the actor back at normal speed. The container write alone
        // is not enough: the game re-drives the container but not every
        // Havok control, so a Poser pause (controls at 0) is released
        // here or not at all.
        ApplySpeedNow(character, 1f);
        return AnimationPortResult.Ok();
    }

    /// <summary>Writes the container speed and every Havok control's
    /// playback speed — the controls are what keep breathing and facial
    /// motion running when only the container is set.</summary>
    private static void ApplySpeedNow(Character* character, float speed)
    {
        character->Timeline.OverallSpeed = speed;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;
        var skeleton = charaBase->Skeleton;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed;
            }
        }
    }

    /// <summary>Rewinds paused controls.</summary>
    public AnimationPortResult RewindPausedControls(ActorId actor)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return AnimationPortResult.Ok();
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return AnimationPortResult.Ok();
        var skeleton = charaBase->Skeleton;

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null)
                    continue;
                if (binding.ptr->Animation.ptr == null)
                    continue;
                if (control->PlaybackSpeed == 0)
                    control->hkaAnimationControl.LocalTime = 0;
            }
        }
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(speed))
            return AnimationPortResult.Fail("Speed must be a finite number.");
        if (!_slotSpeedHookEnabled)
            return AnimationPortResult.Fail(
                "Layer speed is unavailable: the game's slot-speed hook is not active.");

        EnforcementFor(actor).SlotSpeeds[(int)slot] = speed;
        SyncEnforcementIndex();
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
        _log.Information(
            $"[AnimState] native SetSlotSpeed slot={(int)slot} speed={speed:0.##}; "
            + $"field now {character->Timeline.TimelineSequencer.TimelineSpeeds[(int)slot]:0.##}");
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearSlotSpeed(
        ActorId actor, AnimationSlot slot, float restoreSpeed = 1f)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        // Restore before dropping enforcement so the next native frame starts
        // from the value Poser originally observed.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            !enforcement.SlotSpeeds.Remove((int)slot))
            return AnimationPortResult.Ok();

        PruneEnforcement(actor);
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, restoreSpeed);
        return AnimationPortResult.Ok();
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    public AnimationPortResult SetLips(ActorId actor, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        // Must go through the native setter: it does sequencer bookkeeping
        // that a direct field write skips.
        character->Timeline.SetLipsOverrideTimeline(timeline);
        return AnimationPortResult.Ok();
    }

    /// <summary>Changes the actor stance.</summary>
    public AnimationPortResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (_setEmoteMode == null || _cancelTimeline == null)
            return AnimationPortResult.Fail(
                "Stance changes are unavailable: a required game function was not found.");

        var poseType = stance switch
        {
            AnimationStance.SitChair => EmoteController.PoseType.Sit,
            AnimationStance.SitGround => EmoteController.PoseType.GroundSit,
            AnimationStance.Sleeping => EmoteController.PoseType.Doze,
            _ => EmoteController.PoseType.Idle,
        };
        uint emoteMode = stance switch
        {
            AnimationStance.SitChair => EmoteModeSitChair,
            AnimationStance.SitGround => EmoteModeSitGround,
            AnimationStance.Sleeping => EmoteModeSleeping,
            _ => EmoteModeNormal,
        };

        bool weaponDrawn = character->Timeline.IsWeaponDrawn;
        // The explicit pose table remains available while native GPose pose
        // counts settle.
        int wrapped = AnimationTimelines.WrapPose(pose, stance, weaponDrawn);

        bool preserveOffsets = stance == AnimationStance.SitChair;
        var drawOffset = preserveOffsets ? character->DrawOffset : default;
        var cameraOffset = preserveOffsets ? character->CameraOffset : default;

        // Clear the active animation lock.
        if (character->Mode == CharacterModes.AnimLock)
        {
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;
        }
        // Stance playback clears a native Base latch that may predate this
        // session.
        TrySetForcedTimeline(&character->Timeline, 0);

        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        _setEmoteMode(&character->EmoteController, emoteMode);
        character->EmoteController.CurrentPoseType = poseType;
        character->EmoteController.CPoseState = (byte)wrapped;

        if (preserveOffsets)
        {
            character->DrawOffset = drawOffset;
            character->CameraOffset = cameraOffset;
        }

        // Sit and sleep stances are fully carried by the mode change above.
        // Idle is the one family the game does not drive on its own, so its
        // poses are played explicitly — as emotes past index 0, since those
        // poses only exist as emotes.
        if (stance != AnimationStance.Idle)
            return AnimationPortResult.Ok();

        if (wrapped == 0)
        {
            character->Timeline.TimelineSequencer.PlayTimeline(
                weaponDrawn ? AnimationTimelines.BattleIdle : AnimationTimelines.Idle, null);
        }
        else if (weaponDrawn)
        {
            PlayEmoteNative(character, AnimationTimelines.BattlePose);
        }
        else if (wrapped < AnimationTimelines.IdlePoses.Count &&
            AnimationTimelines.IdlePoses[wrapped] is var emote and not 0)
        {
            PlayEmoteNative(character, emote);
        }
        return AnimationPortResult.Ok();
    }

    /// <summary>Sets the weapon animation state.</summary>
    public AnimationPortResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return AnimationPortResult.Ok();
        character->Timeline.TimelineSequencer.PlayTimeline(
            drawn ? AnimationTimelines.DrawWeapon : AnimationTimelines.SheatheWeapon, null);
        character->Timeline.IsWeaponDrawn = drawn;
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Position lock reuses the model transform override that suppresses the
    /// game's per-frame write. Releasing clears only an override created here,
    /// so a placement made with the gizmo survives unlocking.
    /// </summary>
    public AnimationPortResult SetPositionLock(ActorId actor, bool locked)
    {
        if (ResolveActor(actor) is not { } legacy)
            return AnimationPortResult.Fail($"Actor {actor} is no longer available.");

        if (locked)
        {
            if (_posing.HasTransformOverride(legacy))
            {
                // Already held in place by the user's own placement.
                return AnimationPortResult.Ok();
            }
            _posing.SetTransformOverride(legacy, _posing.GetEffectiveTransform(legacy));
            _positionLocks.Add(actor);
            return AnimationPortResult.Ok();
        }

        if (_positionLocks.Remove(actor))
            _posing.ClearTransformOverride(legacy);
        return AnimationPortResult.Ok();
    }

    // ── Scrubbing ─────────────────────────────────────────────────────

    public AnimationPortResult SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(time))
            return AnimationPortResult.Fail("Scrub time must be a finite number.");

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return AnimationPortResult.Fail("Actor has no character skeleton.");
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return AnimationPortResult.Fail("Actor has no character skeleton.");
        var skeleton = charaBase->Skeleton;

        if (control.Partial < 0 || control.Partial >= skeleton->PartialSkeletonCount)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var partial = &skeleton->PartialSkeletons[control.Partial];
        var animated = partial->GetHavokAnimatedSkeleton(0);
        if (animated == null ||
            control.Control < 0 || control.Control >= animated->AnimationControls.Length)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var target = animated->AnimationControls[control.Control].Value;
        if (target == null)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var binding = target->hkaAnimationControl.Binding;
        if (binding.ptr == null || binding.ptr->Animation.ptr == null)
            return AnimationPortResult.Fail("Scrub target no longer exists.");

        // Re-derive the token from the live skeleton: a replacement moves
        // the skeleton or changes the control count, and the write is
        // refused rather than landing on whatever now occupies the slot.
        if (token != 0 && token != CurrentToken(skeleton))
            return AnimationPortResult.Fail("Skeleton changed; scrub cancelled.");

        float duration = binding.ptr->Animation.ptr->Duration;
        target->hkaAnimationControl.LocalTime = Math.Clamp(time, 0f, duration);
        // EVERY partial runs its own control for the same slot (body,
        // face, hair). Scrubbing only one left the others on the old
        // schedule — one of them reaches its clip's end at the old time
        // and the timeline layer resets the whole animation (the
        // pause→scrub→play reset, 2026-09-01). Same index, same time,
        // all partials, each clamped to its own clip.
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            if (p == control.Partial)
                continue;
            var sibling = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (sibling == null || control.Control >= sibling->AnimationControls.Length)
                continue;
            var siblingControl = sibling->AnimationControls[control.Control].Value;
            if (siblingControl == null)
                continue;
            var siblingBinding = siblingControl->hkaAnimationControl.Binding;
            float siblingClip =
                siblingBinding.ptr != null && siblingBinding.ptr->Animation.ptr != null
                    ? siblingBinding.ptr->Animation.ptr->Duration
                    : duration;
            siblingControl->hkaAnimationControl.LocalTime =
                Math.Clamp(time, 0f, siblingClip);
        }
        // The SCHEDULER's clock moves with the scrub — without this the
        // timeline layer keeps counting from the old position and resets
        // the animation on the old schedule. It counts in 30fps FRAMES
        // (dump-proven: control 1.42s ↔ clock 42.74), not seconds.
        var timestamp = SchedulerTimestamp(
            &character->Timeline.TimelineSequencer, control.Control);
        if (timestamp != null)
            *timestamp = Math.Clamp(time, 0f, duration) * 30f;
        return AnimationPortResult.Ok();
    }

    private static ulong CurrentToken(
        FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* skeleton)
    {
        int count = 0;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                count++;
            }
        }
        return unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)count);
    }

    // ── Physics ───────────────────────────────────────────────────────

    public bool IsPhysicsFrozen => _physics.IsFrozen;

    public AnimationPortResult SetPhysicsFrozen(bool frozen) => _physics.SetFrozen(frozen);

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _loops.Clear();
        _forcedLoops.Clear();
        _speedHook?.Dispose();
        _slotSpeedHook?.Dispose();
        _probeTimelineHook?.Dispose();
        _enforcement.Clear();
        _byAddress.Clear();
        // The session restores per-actor overrides before disposal; the
        // global code patch is the patcher's own, and its dispose restores
        // it (or reports the failure explicitly).
        _physics.Dispose();
        GC.SuppressFinalize(this);
    }
}
