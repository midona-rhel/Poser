using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Entities;

namespace Poser.Game.Animation;

/// <summary>
/// The native animation surface the four animation ports share: actor
/// resolution, the signature-resolved entry points, timeline playback and
/// the verified sequencer/scheduler layouts. Owns no per-actor policy.
/// </summary>
public sealed unsafe partial class AnimationNativeState
{
    // Kept for the probe harness (AnimationNativeState.Probe.cs), which
    // arms its own hook lazily.
    private readonly ISigScanner _sigScanner;
    private readonly Dalamud.Plugin.Services.IGameInteropProvider _hooking;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;

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

    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTimeline>? _timelineSheet;
    // The scheduler layer (Ktisis Structs/Animation, offsets cross-checked
    // against our verified sequencer layout): per-slot SchedulerTimeline
    // HANDLES at sequencer+0x70 (Handle = { Data*, Flags }; Flags==0 means
    // dead), and the scheduler's own clock — TimelineController
    // .CurrentTimestamp — at +0x34 of the pointed object. This is the
    // second clock a real scrub must move: the havok controls are only the
    // sampling side, and the scheduler resets the animation on ITS time.
    private const int SequencerSchedulerHandlesOffset = 0x70;
    internal const int SchedulerTimestampOffset = 0x34;

    /// <summary>The slot's live scheduler clock, or null.</summary>
    internal static float* SchedulerTimestamp(
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

    /// <summary>Whether a weapon/prop control runs on the same clock as an
    /// actor control: clip lengths and current times both within a second.</summary>
    internal static bool TracksControl(
        FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl* prop,
        float time, float duration, out float propDuration)
    {
        propDuration = -1f;
        var binding = prop->hkaAnimationControl.Binding;
        if (binding.ptr == null || binding.ptr->Animation.ptr == null)
            return false;
        propDuration = binding.ptr->Animation.ptr->Duration;
        return Math.Abs(propDuration - duration) < 1f
            && Math.Abs(prop->hkaAnimationControl.LocalTime - time) < 1f;
    }

    internal delegate void PropControlVisitor(
        FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl* control);

    /// <summary>Visits every animation control on every attached weapon/prop
    /// draw object.</summary>
    internal static void ForEachPropControl(Character* character, PropControlVisitor visit)
    {
        for (int slotIndex = 0; slotIndex < 3; slotIndex++)
        {
            ref var weapon = ref character->DrawData.Weapon(
                (DrawDataContainer.WeaponSlot)slotIndex);
            var draw = weapon.DrawData.DrawObject;
            if (draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase)
                continue;
            var skeleton = ((CharacterBase*)draw)->Skeleton;
            if (skeleton == null)
                continue;
            for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
            {
                var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
                if (animated == null)
                    continue;
                for (int c = 0; c < animated->AnimationControls.Length; c++)
                {
                    var control = animated->AnimationControls[c].Value;
                    if (control != null)
                        visit(control);
                }
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern bool ReadProcessMemory(
        nint process, nint address, void* buffer, nint size, out nint read);

    /// <summary>Whether a foreign region answers a guarded read.</summary>
    internal static bool RegionReadable(nint address, int size)
    {
        if (size > 0x160)
            return false;
        byte* scratch = stackalloc byte[0x160];
        return ReadProcessMemory((nint)(-1), address, scratch, size, out var read) && read == size;
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
    internal static readonly bool HasForcedTimelineLayout =
        HasForcedTimelineLayoutFor(TimelineSequencerOffset, TimelineContainerSize);

    public AnimationNativeState(
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        IDataManager data)
    {
        _sigScanner = sigScanner;
        _hooking = hooking;
        _timelineSheet = data.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
        _log = log;
        _bindings = bindings;

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

    internal Character* Resolve(ActorId actor, out string? detail)
    {
        detail = null;
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? $"Actor {actor} is no longer available.";
            return null;
        }
        var character = (Character*)legacy.Address;
        return character == null ? null : character;
    }

    internal IActor? ResolveActor(ActorId actor)
    {
        var resolved = _bindings.Resolve(actor);
        return resolved.Success ? resolved.Value : null;
    }

    // ── Native entry points ───────────────────────────────────────────

    internal bool HasCancelTimeline => _cancelTimeline != null;
    internal bool HasSetEmoteMode => _setEmoteMode != null;

    /// <summary>Calls CancelTimeline; callers check <see cref="HasCancelTimeline"/>.</summary>
    internal void CancelTimeline(TimelineContainer* container, nint a2, nint a3) =>
        _cancelTimeline!(container, a2, a3);

    /// <summary>Calls SetEmoteMode; callers check <see cref="HasSetEmoteMode"/>.</summary>
    internal void SetEmoteMode(EmoteController* controller, uint mode) =>
        _setEmoteMode!(controller, mode);

    /// <summary>The slot the sheet's Stance column routes a timeline
    /// onto, or null when the row is missing or unmapped.</summary>
    internal AnimationSlot? TimelineSlot(ushort timeline)
    {
        var stance = _timelineSheet?.GetRowOrDefault(timeline)?.Stance;
        return stance is { } value && AnimationSlots.IsKnown(value)
            ? (AnimationSlot)value
            : null;
    }

    /// <summary>Collects live animation controls.</summary>
    internal static List<ScrubControlReading> CollectControls(Character* character, out ulong token)
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

    // ModeParam is a four-byte native field.
    internal static uint ReadModeParam(Character* character) =>
        *(uint*)((byte*)character + ModeParamOffset);

    internal static void WriteModeParam(Character* character, uint value) =>
        *(uint*)((byte*)character + ModeParamOffset) = value;

    internal Outcome PlayTimeline(Character* character, ushort timeline)
    {
        if (_setTimelineId == null)
            return Outcome.Fail("Timeline playback is unavailable.");

        var mode = character->Mode;
        uint modeParam = ReadModeParam(character);
        ushort baseOverride = character->Timeline.BaseOverride;
        bool hadForced = TryReadForcedTimeline(&character->Timeline, out var forced);
        TrySetForcedTimeline(&character->Timeline, 0);
        if (PlayWithMode(character, timeline))
            return Outcome.Ok();

        character->Mode = mode;
        WriteModeParam(character, modeParam);
        character->Timeline.BaseOverride = baseOverride;
        if (hadForced)
            TrySetForcedTimeline(&character->Timeline, forced);
        return Outcome.Fail("Timeline playback failed.");
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
#if DEBUG
        _probeOurWrite = true;
        try
        {
#endif
            return _setTimelineId!(
                &character->Timeline.TimelineSequencer, timeline, nint.Zero);
#if DEBUG
        }
        finally
        {
            _probeOurWrite = false;
        }
#endif
    }

    /// <summary>Plays an emote through the game entry point.</summary>
    internal bool PlayEmoteNative(Character* character, uint emoteId)
    {
        if (_playEmote == null)
            return false;
        _playEmote(&character->EmoteController, (nint)emoteId, nint.Zero, nint.Zero);
        return true;
    }

    // ── Forced timeline layout ────────────────────────────────────────

    private static bool HasForcedTimelineLayoutFor(
        int timelineSequencerOffset, int timelineContainerSize) =>
        timelineSequencerOffset == 0x10 &&
        ForcedTimelineOffset == timelineSequencerOffset + SequencerForcedTimelineOffset &&
        timelineContainerSize >= timelineSequencerOffset +
            SequencerForcedTimelineOffset + ForcedTimelineSize;

    internal static bool TryReadForcedTimeline(TimelineContainer* container, out ushort timeline)
    {
        if (!HasForcedTimelineLayout)
        {
            timeline = 0;
            return false;
        }
        timeline = *(ushort*)((byte*)container + ForcedTimelineOffset);
        return true;
    }

    internal static bool TrySetForcedTimeline(TimelineContainer* container, ushort timeline)
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
}
