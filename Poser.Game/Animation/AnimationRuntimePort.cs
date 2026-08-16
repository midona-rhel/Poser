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

/// <summary>Reads and updates actor animation state.</summary>
public sealed unsafe class AnimationRuntimePort : IAnimationRuntimePort, IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _posing;

    // State keyed by actor id.
    private readonly Dictionary<ActorId, Enforcement> _enforcement = new();
    // Address index used by speed callbacks.
    private readonly Dictionary<nint, Enforcement> _byAddress = new();
    // Actors whose position lock THIS session created, so releasing it
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

    // These flags report enabled callbacks.
    private readonly bool _overallSpeedHookEnabled;
    private readonly bool _slotSpeedHookEnabled;

    // Entry points used by animation operations.
    private delegate bool SetEmoteModeDelegate(EmoteController* controller, uint mode);
    private readonly SetEmoteModeDelegate? _setEmoteMode;
    private delegate nint CancelTimelineDelegate(TimelineContainer* container, nint a2, nint a3);
    private readonly CancelTimelineDelegate? _cancelTimeline;
    private delegate bool SetTimelineIdDelegate(
        ActionTimelineSequencer* sequencer, ushort timeline, nint arg);
    private readonly SetTimelineIdDelegate? _setTimelineId;
    // Emote callback arguments.
    private delegate bool PlayEmoteDelegate(
        EmoteController* controller, nint emoteId, nint option, nint chair);
    private readonly PlayEmoteDelegate? _playEmote;

    /// <summary>Values passed to the stance call.</summary>
    private const uint EmoteModeNormal = 0;
    private const uint EmoteModeSitGround = 1;
    private const uint EmoteModeSitChair = 2;
    private const uint EmoteModeSleeping = 3;

    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTimeline>? _timelineSheet;

    // The freeze is process-wide.
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
        _timelineSheet = data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
        _framework.Update += EnforceLoops;
        _log = log;
        _bindings = bindings;
        _posing = posing;

        // Missing stance entry points produce an explicit failure.
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

        // Enable each callback independently.
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

        // An unavailable freeze operation returns an explicit failure.
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

    /// <summary>Rebuilds the address index used by speed callbacks.</summary>
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
            _byAddress.TryGetValue(owner, out var enforcement) &&
            enforcement.OverallSpeed is { } speed)
        {
            // Apply the override after the game updates the value.
            container->OverallSpeed = speed;
            return true;
        }
        return result;
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

        // Keep the reported pose family distinct from idle.
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
            slots);
    }

    /// <summary>Reads the current valid controls.</summary>
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

        // The token identifies the current skeleton and control layout.
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

    /// <summary>Finds the current control for a supported slot by slot index.
    /// Empty slots and unsupported slots return null.</summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
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
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null || index >= animated->AnimationControls.Length)
                continue;
            var control = animated->AnimationControls[index].Value;
            if (control == null)
                continue;
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null)
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

    /// <summary>Applies the current mode and starts the selected timeline.
    /// A held mode is cleared before normal playback.</summary>
    public AnimationPortResult Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        if (existing == null)
        {
            captured = new BaseAnimationCapture(
                (byte)character->Mode,
                ReadEmoteMode(character),
                character->Timeline.BaseOverride,
                // Preserve the timeline currently on the base slot.
                character->Timeline.TimelineSequencer.TimelineIds[0],
                ReadForcedTimeline(&character->Timeline));
        }

        return PlayWithMode(character, timeline)
            ? AnimationPortResult.Ok()
            : AnimationPortResult.Fail("The timeline entry point is unavailable.");
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

    /// <summary>Stops the timeline currently driven by the container.</summary>
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

    /// <summary>Captures the current base animation state.</summary>
    public BaseAnimationCapture? CaptureBase(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        return new BaseAnimationCapture(
            (byte)character->Mode,
            ReadEmoteMode(character),
            character->Timeline.BaseOverride,
            character->Timeline.TimelineSequencer.TimelineIds[0],
            ReadForcedTimeline(&character->Timeline));
    }

    private static uint ReadEmoteMode(Character* character) =>
        *(uint*)&character->ModeParam;

    private static void WriteEmoteMode(Character* character, uint mode) =>
        *(uint*)&character->ModeParam = mode;

    /// <summary>Applies the timeline mode and starts the selected timeline.</summary>
    private bool PlayWithMode(Character* character, ushort timeline)
    {
        bool pause = _timelineSheet?.GetRowOrDefault(timeline)?.Pause ?? false;
        if (pause)
        {
            character->Mode = CharacterModes.EmoteLoop;
            WriteEmoteMode(character, 0);
        }
        else if (character->Mode == CharacterModes.EmoteLoop && ReadEmoteMode(character) == 0)
        {
            character->Mode = CharacterModes.Normal;
        }
        else if (character->Mode == CharacterModes.AnimLock)
        {
            character->Mode = CharacterModes.Normal;
            WriteEmoteMode(character, 0);
            character->Timeline.BaseOverride = 0;
        }
        return _setTimelineId != null && _setTimelineId(
            &character->Timeline.TimelineSequencer, timeline, nint.Zero);
    }

    public AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        WriteEmoteMode(character, capture.ModeParam);
        // Restore the captured base timeline, using idle only when empty.
        if (!PlayTimelineNative(
                character,
                capture.BaseSlotTimeline != 0
                    ? capture.BaseSlotTimeline
                    : AnimationTimelines.Idle))
            return AnimationPortResult.Fail("The timeline entry point is unavailable.");
        WriteForcedTimeline(&character->Timeline, capture.ForcedTimeline);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult PlayEmote(ActorId actor, uint emoteId)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        return PlayEmoteNative(character, emoteId)
            ? AnimationPortResult.Ok()
            : AnimationPortResult.Fail("The emote entry point is unavailable.");
    }

    // The persistent timeline field is 0x2E0 bytes after TimelineContainer.
    private const int ActionTimelineIdOffset = 0x2E0;

    private static ushort ReadForcedTimeline(TimelineContainer* timeline) =>
        *(ushort*)((byte*)timeline + ActionTimelineIdOffset);

    private static void WriteForcedTimeline(
        TimelineContainer* timeline,
        ushort value) =>
        *(ushort*)((byte*)timeline + ActionTimelineIdOffset) = value;

    public bool SupportsForceLoop => _setTimelineId != null;

    /// <summary>Writes the game's persistent action timeline field. The
    /// actor is resolved again for every write.</summary>
    public AnimationPortResult SetForceLoop(ActorId actor, ushort timeline)
    {
        if (_setTimelineId == null)
            return AnimationPortResult.Fail("Persistent animation looping is unavailable.");
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        WriteForcedTimeline(&character->Timeline, timeline);
        return AnimationPortResult.Ok();
    }

    private bool PlayTimelineNative(Character* character, ushort timeline) =>
        _setTimelineId != null && _setTimelineId(
            &character->Timeline.TimelineSequencer, timeline, nint.Zero);

    /// <summary>Plays an emote through the emote entry point.</summary>
    private bool PlayEmoteNative(Character* character, uint emoteId)
    {
        if (_playEmote == null)
        {
            PlayTimelineNative(character, AnimationTimelines.Idle);
            return false;
        }
        _playEmote(&character->EmoteController, (nint)emoteId, nint.Zero, nint.Zero);
        return true;
    }

    // ── Loops ───────────────────────────────────────────

    /// <summary>One armed loop and its replay cooldown.</summary>
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

    public void ClearLoops(ActorId actor) => _loops.Remove(actor);

    /// <summary>Replays an armed slot when its timeline has ended.</summary>
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
                    PlayWithMode(character, arm.Timeline);
                    arm.Cooldown = LoopCooldownTicks;
                }
            }
        }
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
        // Speed overrides require an enabled callback.
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

        // Release only speed that this port currently enforces.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            enforcement.OverallSpeed == null)
            return AnimationPortResult.Ok();

        enforcement.OverallSpeed = null;
        PruneEnforcement(actor);
        // Restore the container and all live controls to normal speed.
        ApplySpeedNow(character, 1f);
        return AnimationPortResult.Ok();
    }

    /// <summary>Writes speed to the container and its live controls.</summary>
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

    /// <summary>Rewinds paused controls to their first frame. Missing draw
    /// objects and skeletons have nothing to rewind.</summary>
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
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearSlotSpeed(ActorId actor, AnimationSlot slot)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        // Only release speed that this port currently enforces.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            !enforcement.SlotSpeeds.Remove((int)slot))
            return AnimationPortResult.Ok();

        PruneEnforcement(actor);
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, 1f);
        return AnimationPortResult.Ok();
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    public AnimationPortResult SetLips(ActorId actor, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        // Use the setter so the sequencer updates its state.
        character->Timeline.SetLipsOverrideTimeline(timeline);
        return AnimationPortResult.Ok();
    }

    /// <summary>Changes stance after cancelling the active timeline and
    /// restores chair offsets when needed.</summary>
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

        // Keep idle poses within the available emote entries.
        int available = EmoteController.GetAvailablePoses(poseType);
        if (available <= 0)
            available = 1;
        if (stance == AnimationStance.Idle)
            available = Math.Min(available, AnimationTimelines.IdlePoses.Count);
        int wrapped = ((pose % available) + available) % available;

        bool preserveOffsets = stance == AnimationStance.SitChair;
        var drawOffset = preserveOffsets ? character->DrawOffset : default;
        var cameraOffset = preserveOffsets ? character->CameraOffset : default;

        // Clear a prior base latch before changing stance.
        if (character->Mode == CharacterModes.AnimLock)
        {
            character->Mode = CharacterModes.Normal;
            WriteEmoteMode(character, 0);
            character->Timeline.BaseOverride = 0;
        }

        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        _setEmoteMode(&character->EmoteController, emoteMode);
        character->EmoteController.CurrentPoseType = poseType;
        character->EmoteController.CPoseState = (byte)wrapped;

        if (preserveOffsets)
        {
            character->DrawOffset = drawOffset;
            character->CameraOffset = cameraOffset;
        }

        // Non-idle stances are complete after the mode change.
        if (stance != AnimationStance.Idle)
            return AnimationPortResult.Ok();

        bool weaponDrawn = character->Timeline.IsWeaponDrawn;
        if (wrapped == 0)
        {
            if (!PlayTimelineNative(
                    character,
                    weaponDrawn ? AnimationTimelines.BattleIdle : AnimationTimelines.Idle))
                return AnimationPortResult.Fail("The timeline entry point is unavailable.");
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

    /// <summary>Plays the draw or sheathe timeline and updates the weapon
    /// state flag.</summary>
    public AnimationPortResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return AnimationPortResult.Ok();
        if (!PlayTimelineNative(
                character,
                drawn ? AnimationTimelines.DrawWeapon : AnimationTimelines.SheatheWeapon))
            return AnimationPortResult.Fail("The timeline entry point is unavailable.");
        character->Timeline.IsWeaponDrawn = drawn;
        return AnimationPortResult.Ok();
    }

    /// <summary>Uses the existing transform override for position lock.</summary>
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

        // Reject writes when the skeleton or control layout changed.
        if (token != 0 && token != CurrentToken(skeleton))
            return AnimationPortResult.Fail("Skeleton changed; scrub cancelled.");

        float duration = binding.ptr->Animation.ptr->Duration;
        target->hkaAnimationControl.LocalTime = Math.Clamp(time, 0f, duration);
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
        _framework.Update -= EnforceLoops;
        _loops.Clear();
        _speedHook?.Dispose();
        _slotSpeedHook?.Dispose();
        _enforcement.Clear();
        _byAddress.Clear();
        _physics.Dispose();
        GC.SuppressFinalize(this);
    }
}
