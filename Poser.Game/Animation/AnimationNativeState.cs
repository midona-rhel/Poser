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
    // FUN_1408377F0(id): the in-memory ActionTimeline row, or 0. Pending a
    // patchcheck entry; unique on the analysed build (sig.py, 1 hit).
    private delegate nint ActionTimelineRowDelegate(uint id);
    private readonly ActionTimelineRowDelegate? _actionTimelineRow;
    // This native entry point takes four arguments.
    private delegate bool PlayEmoteDelegate(
        EmoteController* controller, nint emoteId, nint option, nint chair);
    private readonly PlayEmoteDelegate? _playEmote;

    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTimeline>? _timelineSheet;
    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Emote>? _emoteSheet;
    // TimelineContainer::PlayActionTimeline 0x1408BED60(container, first, second, params):
    // queues first (an emote's intro) and second (its loop) and plays first; the
    // sequencer plays second itself when first ends (the game's own intro → loop).
    // Pending a patchcheck entry; unique on the analysed build (sig.py, 1 hit).
    private delegate void PlayActionTimelineDelegate(TimelineContainer* container, ushort first, ushort second, nint parameters);
    private readonly PlayActionTimelineDelegate? _playActionTimeline;
    // EmoteController::GetPosture 0x140616160 (patchcheck EmoteController.GetPosture):
    // 0 standing, 1 ground sit, 2 chair, 3 mounted, 4 dozing, 5 swimming, 6 diving,
    // from Character +0x688 (mount), +0x628 bit 5 (swim), diving, Mode/ModeParam.
    // A pure read, valid on any character.
    private delegate int GetPostureDelegate(EmoteController* controller);
    private readonly GetPostureDelegate? _getPosture;
    // EmoteController::CanPlayEmote 0x140614FE0 (patchcheck EmoteController.CanPlayEmote):
    // the game's emote permission. For a GPose copy: the row exists, the mode table
    // allows it, the posture allows it (mounted/swimming/diving need the row's bits)
    // and a mount allows emotes. Flags 0: no slot-priority check. A pure read.
    private delegate byte CanPlayEmoteDelegate(EmoteController* controller, ushort emoteId, uint flags);
    private readonly CanPlayEmoteDelegate? _canPlayEmote;
    // EmoteController::SetPose 0x140614950 (patchcheck EmoteController.SetPose): the
    // game's /changepose step without its server commands. Index 1..n plays the kind's
    // pose emote (StartEmoteLocal: the emote row's own mode), 0 stops it back to the base
    // idle; it stores the kind (+0x20) and index (+0x21, CPoseState) after playing.
    // No network in GPose. Kinds: 0 idle, 1 weapon drawn, 2 chair, 3 ground sit, 4 doze.
    private delegate void SetPoseDelegate(EmoteController* controller, int kind, byte index, nint options);
    private readonly SetPoseDelegate? _setPose;
    // Character::SetMode 0x140909170 (patchcheck Character.SetMode): writes Mode and
    // ModeParam with no gate and no emote side effects (EmoteController::SetMode's exit
    // path is what plays end emotes).
    private delegate void CharacterSetModeDelegate(Character* character, int mode, int param);
    private readonly CharacterSetModeDelegate? _characterSetMode;
    // EmoteController +0x35: set when a non-local actor leaves a loop mode with no end
    // emote; in Normal mode the next base-idle pick then cancels the emote and clears the
    // pose. Only PlayEmote clears it, StartEmoteLocal and SetPose don't.
    private const int EmoteCancelPendingOffset = 0x35;
    private readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.EmoteMode>? _emoteModeSheet;
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

    /// <summary>The slot's live SchedulerTimeline, or 0.</summary>
    internal static nint SlotTimeline(ActionTimelineSequencer* sequencer, int slot)
    {
        var timestamp = SchedulerTimestamp(sequencer, slot);
        return timestamp == null ? 0 : (nint)timestamp - SchedulerTimestampOffset;
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
        _emoteSheet = data.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        _emoteModeSheet = data.GetExcelSheet<Lumina.Excel.Sheets.EmoteMode>();
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
        _playActionTimeline = ScanDelegate<PlayActionTimelineDelegate>(
            sigScanner,
            "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 49 8B E9 41 0F B7 D8 48 8B F1 E8 ?? ?? ?? ?? 0F B7 D3 48 8B CE 0F B7 F8 E8",
            "PlayActionTimeline");
        _getPosture = ScanDelegate<GetPostureDelegate>(
            sigScanner, "40 53 48 83 EC 20 48 8B D9 48 8B 49 08 48 85 C9 0F 84", "EmoteController.GetPosture");
        _canPlayEmote = ScanDelegate<CanPlayEmoteDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_CanPlayEmote, "EmoteController.CanPlayEmote");
        _setPose = ScanDelegate<SetPoseDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_SetPose, "EmoteController.SetPose");
        _characterSetMode = ScanDelegate<CharacterSetModeDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.Character_SetMode, "Character.SetMode");
        _actionTimelineRow = ScanDelegate<ActionTimelineRowDelegate>(
            sigScanner,
            "48 83 EC 28 8D 41 FF 3D E7 FD 00 00 77 ?? 48 8B 05 ?? ?? ?? ?? 44 8B C1 BA 64 00 00 00 48 8B 88 40 2B 00 00 E8",
            "ActionTimelineRow");
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
    internal bool HasSetPose => _setPose != null && _characterSetMode != null;

    /// <summary>The game's pose step; callers check <see cref="HasSetPose"/>.</summary>
    internal void SetPose(Character* character, int kind, byte index) =>
        _setPose!(&character->EmoteController, kind, index, nint.Zero);

    /// <summary>Character::SetMode; available with <see cref="HasSetPose"/>.</summary>
    internal void SetCharacterMode(Character* character, CharacterModes mode, byte param) =>
        _characterSetMode!(character, (int)mode, param);

    /// <summary>Clears the controller's pending emote cancel, as PlayEmote does.</summary>
    internal static void ClearEmoteCancelPending(Character* character) =>
        *((byte*)&character->EmoteController + EmoteCancelPendingOffset) = 0;

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

    internal Outcome PlayTimeline(Character* character, ushort timeline)
    {
        if (_setTimelineId == null)
            return Outcome.Fail("Timeline playback is unavailable.");

        var mode = character->Mode;
        byte modeParam = character->ModeParam;
        ushort baseOverride = character->Timeline.BaseOverride;
        bool hadForced = TryReadForcedTimeline(&character->Timeline, out var forced);
        TrySetForcedTimeline(&character->Timeline, 0);
        if (PlayWithMode(character, timeline))
            return Outcome.Ok();

        character->Mode = mode;
        character->ModeParam = modeParam;
        character->Timeline.BaseOverride = baseOverride;
        if (hadForced)
            TrySetForcedTimeline(&character->Timeline, forced);
        return Outcome.Fail("Timeline playback failed.");
    }

    /// <summary>Applies the timeline mode before native playback.</summary>
    private bool PlayWithMode(Character* character, ushort timeline)
    {
        PrepareMode(character, timeline);
        return WithUpperKept(timeline, () => _setTimelineId!(
            &character->Timeline.TimelineSequencer, timeline, nint.Zero));
    }

    private void PrepareMode(Character* character, ushort timeline)
    {
        bool pause = _timelineSheet?.GetRowOrDefault(timeline)?.Pause ?? false;
        if (pause)
        {
            character->Mode = CharacterModes.EmoteLoop;
            character->ModeParam = 0;
        }
        else if (character->Mode == CharacterModes.EmoteLoop && character->ModeParam == 0
            && TimelineSlot(timeline) is AnimationSlot.Base or null)
        {
            // Only a Full body play leaves the loop: a standing pose holds EmoteLoop 0
            // (the base-idle pick runs in Normal mode only), and an expression or Upper
            // body layer on it must not end it.
            character->Mode = CharacterModes.Normal;
        }
        else if (character->Mode == CharacterModes.AnimLock)
        {
            // Clear the active animation lock.
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;
        }
    }

    /// <summary>
    /// A row with +0x14 bit 2 (the additives measured: add_yes, add_no) makes
    /// the play stop the Upper Body slot first (FUN_140606810). Layers are
    /// independent, so unless the row plays on Upper Body itself the bit is
    /// cleared for this call only and restored: nothing else in the call reads
    /// it (the transition doesn't; StartSlot reads bit 0), and it all runs on
    /// this thread.
    /// </summary>
    private bool WithUpperKept(ushort timeline, Func<bool> play)
    {
        byte* rowFlags = null;
        if (_actionTimelineRow != null && TimelineSlot(timeline) != AnimationSlot.UpperBody)
        {
            nint row = _actionTimelineRow(timeline);
            if (row != 0 && (*(byte*)(row + 0x14) & 4) != 0)
            {
                rowFlags = (byte*)(row + 0x14);
                *rowFlags &= unchecked((byte)~4);
            }
        }
#if DEBUG
        _probeOurWrite = true;
#endif
        try
        {
            return play();
        }
        finally
        {
            if (rowFlags != null)
                *rowFlags |= 4;
#if DEBUG
            _probeOurWrite = false;
#endif
        }
    }

    /// <summary>
    /// Whether the game would let the actor enter this emote mode (sit on the ground,
    /// sit, sleep) from how it is now. Only mounted, swimming or diving can refuse:
    /// there the game's own permission check answers for the mode's start emote (none
    /// of them is allowed, a mount that forbids emotes refuses too). Any other posture
    /// is allowed, because the stance change cancels the current one first; the check
    /// itself would judge it from the stance being left (sit to ground sit refused).
    /// Null when the check or the row is unavailable.
    /// </summary>
    internal bool? CanStartEmoteMode(Character* character, uint emoteMode)
    {
        if (_canPlayEmote == null || _getPosture == null
            || _emoteModeSheet?.GetRowOrDefault(emoteMode) is not { } mode || mode.StartEmote.RowId == 0)
            return null;
        int posture = _getPosture(&character->EmoteController);
        if (posture is not (3 or 5 or 6))
            return true;
        return _canPlayEmote(&character->EmoteController, (ushort)mode.StartEmote.RowId, 0) != 0;
    }

    /// <summary>
    /// The emote's row for the actor's posture, as the game's emote start picks it
    /// (FUN_140617920 over GetPosture): standing [0], ground sit [2], chair [3],
    /// dozing [5]; mounted, swimming or diving [4] only if the emote is usable so.
    /// 0 where the game refuses the emote in this posture; null when the posture
    /// can't be read. The standing height-adjust loop [6] is not applied.
    /// </summary>
    internal ushort? EmotePostureRow(Character* character, uint emoteId)
    {
        if (_getPosture == null || _emoteSheet?.GetRowOrDefault(emoteId) is not { } emote
            || emote.ActionTimeline.Count < 6)
            return null;
        int index = _getPosture(&character->EmoteController) switch
        {
            1 => 2,
            2 => 3,
            4 => 5,
            // Row +0x28 bits 0-2 (EXDSchema UsableWhenMounted/Swimming/Diving;
            // this Lumina's Unknown1-3, in bit order before DrawsWeapon's bit 6).
            3 => emote.Unknown1 ? 4 : -1,
            5 => emote.Unknown2 ? 4 : -1,
            6 => emote.Unknown3 ? 4 : -1,
            _ => 0,
        };
        return index < 0 ? (ushort)0 : (ushort)emote.ActionTimeline[index].RowId;
    }

    /// <summary>
    /// An emote from its start, the way the game's emote start ends up playing it:
    /// PlayActionTimeline(intro = Emote.ActionTimeline[1], loop); the game hands
    /// over to the loop itself. The loop is the caller's (the posture's row, or
    /// [0]): the intro is the same for every posture. The emote controller's own start (the old
    /// path) checks a slot priority that Poser's earlier plays leave set, and for
    /// any actor but the real local player returns true without playing.
    /// Expression emotes take another path and are not handled here. True only if
    /// the base slot actually changed.
    /// </summary>
    internal bool PlayEmoteFromStart(Character* character, uint emoteId, ushort loop)
    {
        if (_playActionTimeline == null || _emoteSheet?.GetRowOrDefault(emoteId) is not { } emote
            || emote.ActionTimeline.Count < 2)
            return false;
        if (loop == 0)
            loop = (ushort)emote.ActionTimeline[0].RowId;
        ushort intro = (ushort)emote.ActionTimeline[1].RowId;
        ushort first = intro != 0 ? intro : loop;
        if (first == 0)
            return false;
        var sequencer = &character->Timeline.TimelineSequencer;
        nint before = SlotTimeline(sequencer, 0);
        // The mode the emote settles in comes from its loop row (an emote-loop row
        // puts the character in EmoteLoop; in Normal mode the intro alone was
        // replaced by idle at once).
        PrepareMode(character, loop != 0 ? loop : first);
        WithUpperKept(first, () =>
        {
            _playActionTimeline(&character->Timeline, first, intro != 0 ? loop : (ushort)0, nint.Zero);
            return true;
        });
        return SlotTimeline(sequencer, 0) != before;
    }

    /// <summary>Plays an emote through the game entry point.</summary>
    internal bool PlayEmoteNative(Character* character, uint emoteId)
    {
        if (_playEmote == null)
            return false;
        // The game refuses an emote it can't start now; that answer used to be
        // dropped, so a refused Apply reported success and played nothing.
        return _playEmote(&character->EmoteController, (nint)emoteId, nint.Zero, nint.Zero);
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
