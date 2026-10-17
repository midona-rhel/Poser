using System;
using System.Collections.Generic;
using System.Linq;
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
    // EmoteController::StopLoopedEmote 0x1406145A0 (patchcheck EmoteController.StopLoopedEmote):
    // in a loop mode, CancelEmote (the emote's slot stopped, ClearPose) and then Normal
    // mode for client objects; with no emote playing it only clears the pose.
    private delegate void StopLoopedEmoteDelegate(EmoteController* controller, nint flags);
    private readonly StopLoopedEmoteDelegate? _stopLoopedEmote;
    private readonly GPoseMotionRecords _gposeMotion;
    // TimelineContainer::SetWeaponDrawn 0x1408BFBC0 (patchcheck TimelineContainer.SetWeaponDrawn):
    // Flags3 bit 6, a looped emote stopped, and the draw (1) or sheathe (2) row through
    // the queued play; the ornament rider too.
    private delegate void SetWeaponDrawnDelegate(TimelineContainer* container, byte drawn, byte playAnimation);
    private readonly SetWeaponDrawnDelegate? _setWeaponDrawn;
    // EmoteController.CPoseTable (patchcheck DAT): 8 kinds x 0x20 bytes, +0x00 u16* pose
    // emotes, +0x08 u16 base timeline, +0x10 predicate, +0x18 u8 emote count.
    private readonly byte* _cposeTable;
    // EmoteController.PlayEmoteOptionVtbl (patchcheck DAT): nothing calls through it,
    // it is set so the option is the game's own object.
    private readonly nint _playEmoteOptionVtbl;
    private const int CPoseKinds = 8, CPoseEntrySize = 0x20, CPoseCountOffset = 0x18;
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

    /// <summary>The row the slot was asked to play (ClientStructs TimelineIds, seq+0xE0):
    /// the one the base pick compares. For slot 0 it runs ahead of
    /// <see cref="StartedId"/> while the row is queued.</summary>
    internal static ushort RequestedId(ActionTimelineSequencer* sequencer, int slot) =>
        sequencer->TimelineIds[slot];

    /// <summary>The row StartSlot last started on the slot (seq+0xFC).</summary>
    internal static ushort StartedId(ActionTimelineSequencer* sequencer, int slot) =>
        *(ushort*)((byte*)sequencer + 0xFC + 2 * slot);

    /// <summary>Slot 0's queued second row (seq+0x152; an emote's loop after its intro).</summary>
    internal static ushort QueuedId(ActionTimelineSequencer* sequencer) =>
        *(ushort*)((byte*)sequencer + 0x152);

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
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_SetMode, "SetEmoteMode");
        _cancelTimeline = ScanDelegate<CancelTimelineDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.TimelineContainer_CancelTimeline, "CancelTimeline");
        _setTimelineId = ScanDelegate<SetTimelineIdDelegate>(
            sigScanner,
            Poser.Game.Natives.NativeSignatures.ActionTimelineSequencer_PlayTimeline,
            "SetTimelineId");
        _playActionTimeline = ScanDelegate<PlayActionTimelineDelegate>(
            sigScanner,
            Poser.Game.Natives.NativeSignatures.TimelineContainer_PlayActionTimeline,
            "PlayActionTimeline");
        _getPosture = ScanDelegate<GetPostureDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_GetPosture, "EmoteController.GetPosture");
        _canPlayEmote = ScanDelegate<CanPlayEmoteDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_CanPlayEmote, "EmoteController.CanPlayEmote");
        _setPose = ScanDelegate<SetPoseDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_SetPose, "EmoteController.SetPose");
        _characterSetMode = ScanDelegate<CharacterSetModeDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.Character_SetMode, "Character.SetMode");
        _setWeaponDrawn = ScanDelegate<SetWeaponDrawnDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.TimelineContainer_SetWeaponDrawn, "TimelineContainer.SetWeaponDrawn");
        if (Poser.Game.Natives.NativeData.TryResolve(sigScanner, Poser.Game.Natives.NativeData.EmoteController_PlayEmoteOptionVtbl, out var optionVtbl))
            _playEmoteOptionVtbl = optionVtbl;
        if (Poser.Game.Natives.NativeData.TryResolve(sigScanner, Poser.Game.Natives.NativeData.EmoteController_CPoseTable, out var cposeTable))
            _cposeTable = (byte*)cposeTable;
        else
            _log.Warning("Animation: the pose table was not found; stance poses can't be stepped.");
        _gposeMotion = new GPoseMotionRecords(sigScanner, log);
        _stopLoopedEmote = ScanDelegate<StopLoopedEmoteDelegate>(
            sigScanner, Poser.Game.Natives.NativeSignatures.EmoteController_StopLoopedEmote, "EmoteController.StopLoopedEmote");
        _actionTimelineRow = ScanDelegate<ActionTimelineRowDelegate>(
            sigScanner,
            Poser.Game.Natives.NativeSignatures.Sheet_GetActionTimelineRow,
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

    /// <summary>A pose kind's pose count (the base pose and each pose emote) from the
    /// game's table, or null when the table isn't available.</summary>
    internal int? PoseCount(int kind) =>
        _cposeTable == null || kind is < 0 or >= CPoseKinds
            ? null
            : 1 + _cposeTable[kind * CPoseEntrySize + CPoseCountOffset];

    /// <summary>The game's weapon draw or sheathe; false when it wasn't found.</summary>
    internal bool SetWeaponDrawn(Character* character, bool drawn)
    {
        if (_setWeaponDrawn == null)
            return false;
        _setWeaponDrawn(&character->Timeline, drawn ? (byte)1 : (byte)0, 1);
        return true;
    }

    /// <summary>A standing loop with no EmoteMode (EmoteLoop 0): a loop mode is
    /// switched loop to loop, which runs no exit hook. Not for the real player.</summary>
    internal void EnterStandingLoop(Character* character)
    {
        if (character->Mode != CharacterModes.EmoteLoop || character->ModeParam != 0)
            _characterSetMode!(character, (int)CharacterModes.EmoteLoop, 0);
    }

    /// <summary>Normal mode from a loop mode without its stand-up: through EmoteLoop 0,
    /// whose exit has no end emote; the pending emote cancel that exit sets is cleared.
    /// Not for the real player.</summary>
    internal void LeaveLoopMode(Character* character)
    {
        if (character->Mode is not (CharacterModes.EmoteLoop or CharacterModes.InPositionLoop))
            return;
        if (character->ModeParam != 0)
            _characterSetMode!(character, (int)CharacterModes.EmoteLoop, 0);
        _characterSetMode!(character, (int)CharacterModes.Normal, 0);
        ClearEmoteCancelPending(character);
    }

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

    /// <summary>
    /// Plays a row through ActionTimelineSequencer::PlayTimeline the way the game would
    /// (RE notes/poser-animation-model.md §1). A layer plays as it is. A Full body row
    /// plays in a Normal-family mode, a loop mode left without its stand-up and its pose
    /// cleared. Unless BaseOverride already repeats it, it also plays at priority 1 or
    /// more, so the base-idle pick leaves it until it ends, as the game does with actions.
    /// </summary>
    internal Outcome PlayTimeline(Character* character, ushort timeline) =>
        Play(character, timeline, leaveLoop: true);

    /// <summary>A row played again as it was (a redraw's replay): the mode stays.</summary>
    internal Outcome ReplayTimeline(Character* character, ushort timeline) =>
        Play(character, timeline, leaveLoop: false);

    private Outcome Play(Character* character, ushort timeline, bool leaveLoop)
    {
        if (_setTimelineId == null || _characterSetMode == null)
            return Outcome.Fail("Timeline playback is unavailable.");
        if (_timelineSheet?.GetRowOrDefault(timeline) == null)
            return Outcome.Fail("Timeline playback failed: the game has no such animation.");
        bool fullBody = TimelineSlot(timeline) is AnimationSlot.Base or null;
        if (fullBody && leaveLoop)
        {
            _gposeMotion.TakeOver(character);
            LeaveForFullBody(character);
        }
        var sequencer = &character->Timeline.TimelineSequencer;
        if (!WithUpperKept(timeline, () => _setTimelineId(sequencer, timeline, nint.Zero)))
            return Outcome.Fail("Timeline playback failed.");
        if (fullBody && character->Timeline.BaseOverride != timeline && RequestedId(sequencer, 0) == timeline)
            HoldAgainstBasePick(sequencer);
        return Outcome.Ok();
    }

    // seq+0x54 (i32): slot 0's current priority. The immediate play writes the row's;
    // the queued plays (the base-idle pick, emotes) are refused above theirs. ClearSlot
    // sets it to -1 when the row ends.
    private const int Slot0PriorityOffset = 0x54;

    /// <summary>A priority-0 row (loops, idles, most Pause rows) would be replaced by
    /// the base-idle pick on the next frame: priority 1 keeps it, as a play-params
    /// priority override would.</summary>
    private static void HoldAgainstBasePick(ActionTimelineSequencer* sequencer)
    {
        int* priority = (int*)((byte*)sequencer + Slot0PriorityOffset);
        if (*priority < 1)
            *priority = 1;
    }

    /// <summary>
    /// What a Full body play needs from the mode: a loop mode (sitting, an emote loop, a
    /// standing pose) is left through EmoteLoop 0, so its exit hook plays no stand-up,
    /// and the game's stop for a looped emote clears the pose. An animation lock goes
    /// to Normal. Mounted and the other Normal-family modes stay.
    /// </summary>
    private void LeaveForFullBody(Character* character)
    {
        if (IsRealPlayer(character))
            return;
        if (character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)
        {
            if (character->ModeParam != 0)
                _characterSetMode!(character, (int)CharacterModes.EmoteLoop, 0);
            _stopLoopedEmote?.Invoke(&character->EmoteController, nint.Zero);
            if (character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)
                _characterSetMode!(character, (int)CharacterModes.Normal, 0);
            ClearEmoteCancelPending(character);
        }
        else if (character->Mode == CharacterModes.AnimLock)
        {
            _characterSetMode!(character, (int)CharacterModes.Normal, 0);
            character->Timeline.BaseOverride = 0;
        }
    }

    /// <summary>
    /// The real player (it can be adopted into GPose) is the game's local actor: its
    /// mode and emote exits are the player's own input, and they reach the server
    /// (StopLoopedEmote stands it up with 0x1F7/0x1F8, ClearPose sends 0x1FB). Poser
    /// changes no mode and stops no emote on it, only plays rows.
    /// </summary>
    internal static bool IsRealPlayer(Character* character) =>
        (nint)character == (nint)Control.Instance()->LocalPlayer;

    /// <summary>
    /// The Full body back to a captured state the game's way (§2): BaseOverride, then
    /// the slot cleared, so the base-idle pick (Normal-family modes) or the EmoteMode
    /// upkeep (loop modes) plays what the game would. A mode Poser changed is left
    /// first; the captured one is entered again. EmoteLoop 0 has no upkeep, so its row
    /// is played again. A capture in the mode the actor is still in (a layer reset's
    /// own snapshot) keeps the emote and pose it runs.
    /// </summary>
    internal Outcome RestoreFullBody(Character* character, CharacterModes mode, byte param,
        ushort baseOverride, ushort slotTimeline)
    {
        if (_cancelTimeline == null || _characterSetMode == null)
            return Outcome.Fail("Full body reset is unavailable: a required game function was not found.");
        bool loop = mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop;
        bool sameMode = character->Mode == mode && character->ModeParam == param;
        if (!sameMode)
            LeaveForFullBody(character);
        character->Timeline.BaseOverride = loop ? (ushort)0 : baseOverride;
        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        if (!loop || IsRealPlayer(character))
        {
            _gposeMotion.GiveBack(character, mode, param, baseOverride, slotTimeline);
            return Outcome.Ok();
        }
        if (!sameMode)
        {
            // The chair's enter hook sets the sit height into DrawOffset/CameraOffset;
            // the placement Poser gave the actor stays.
            var drawOffset = character->DrawOffset;
            var cameraOffset = character->CameraOffset;
            _characterSetMode(character, (int)mode, param);
            character->DrawOffset = drawOffset;
            character->CameraOffset = cameraOffset;
            ClearEmoteCancelPending(character);
        }
        var replayed = param == 0 && slotTimeline != 0 ? ReplayTimeline(character, slotTimeline) : Outcome.Ok();
        _gposeMotion.GiveBack(character, mode, param, baseOverride, slotTimeline);
        return replayed;
    }

    /// <summary>Stops GPose replaying the actor's recorded motion before a stance
    /// takes the Full body.</summary>
    internal void TakeOverGPoseMotion(Character* character) => _gposeMotion.TakeOver(character);

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

    /// <summary>The game's posture (EmoteController::GetPosture): 0 standing, 1 ground
    /// sit, 2 chair, 3 mounted, 4 dozing, 5 swimming, 6 diving; null when unavailable.</summary>
    internal int? Posture(Character* character) =>
        _getPosture == null ? null : _getPosture(&character->EmoteController);

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
    /// An emote from its start with the game's own emote start (RE
    /// notes/poser-animation-model.md §1.1): EmoteController::PlayEmote picks the
    /// posture's rows, plays the intro and hands over to the loop, enters the emote's
    /// EmoteMode and clears the pending emote cancel. A pose emote or a loop with no
    /// EmoteMode holds EmoteLoop 0 first, since the Normal-mode base-idle pick replaces
    /// it; the start is refused above priority 1, so a higher row Poser played is
    /// cleared first. The real player only plays its rows: PlayEmote starts nothing
    /// for the local actor, and Poser changes no mode on it. Expression emotes take
    /// another path. True when slot 0 was given the emote's row.
    /// </summary>
    internal bool PlayEmoteFromStart(Character* character, uint emoteId, ushort loop)
    {
        if (_characterSetMode == null || _cancelTimeline == null
            || _emoteSheet?.GetRowOrDefault(emoteId) is not { } emote
            || emote.ActionTimeline.Count < 2)
            return false;
        if (IsRealPlayer(character))
            return PlayEmoteRows(character, emote, loop);
        ushort intro = (ushort)emote.ActionTimeline[1].RowId;
        ushort standingLoop = (ushort)emote.ActionTimeline[0].RowId;
        ushort first = intro != 0 ? intro : standingLoop;
        if (first == 0)
            return false;
        var sequencer = &character->Timeline.TimelineSequencer;
        _gposeMotion.TakeOver(character);
        LeaveForFullBody(character);
        if (emote.EmoteMode.RowId == 0
            && (IsPoseEmote(emoteId) || _timelineSheet?.GetRowOrDefault(standingLoop)?.Pause == true))
            _characterSetMode(character, (int)CharacterModes.EmoteLoop, 0);
        if (*(int*)((byte*)sequencer + Slot0PriorityOffset) > 1)
            _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        var option = stackalloc EmoteController.PlayEmoteOption[1];
        *option = default;
        *(nint*)option = _playEmoteOptionVtbl;
        option->TargetId = 0xE0000000;
        // Bit 0: no chat line and no target lookup.
        option->Flags = 1;
        var controller = &character->EmoteController;
        bool started = WithUpperKept(first, () => controller->PlayEmote(emoteId, option));
        return started && RequestedId(sequencer, 0) != 0
            && RequestedId(sequencer, 0) is var row
            && emote.ActionTimeline.Any(t => t.RowId == row);
    }

    /// <summary>The real player's emote from its start: its intro and loop rows
    /// queued on its own timeline (TimelineContainer::PlayActionTimeline), the loop
    /// the caller's posture row.</summary>
    private bool PlayEmoteRows(Character* character, Lumina.Excel.Sheets.Emote emote, ushort loop)
    {
        if (_playActionTimeline == null)
            return false;
        if (loop == 0)
            loop = (ushort)emote.ActionTimeline[0].RowId;
        ushort intro = (ushort)emote.ActionTimeline[1].RowId;
        ushort first = intro != 0 ? intro : loop;
        if (first == 0)
            return false;
        var sequencer = &character->Timeline.TimelineSequencer;
        nint before = SlotTimeline(sequencer, 0);
        WithUpperKept(first, () =>
        {
            _playActionTimeline(&character->Timeline, first, intro != 0 ? loop : (ushort)0, nint.Zero);
            return true;
        });
        return SlotTimeline(sequencer, 0) != before;
    }

    /// <summary>Whether an emote is one of the game's /changepose poses (the pose
    /// table's emote lists).</summary>
    private bool IsPoseEmote(uint emoteId)
    {
        if (_cposeTable == null)
            return false;
        for (int kind = 0; kind < CPoseKinds; kind++)
        {
            byte* entry = _cposeTable + kind * CPoseEntrySize;
            ushort* emotes = *(ushort**)entry;
            int count = entry[CPoseCountOffset];
            for (int i = 0; emotes != null && i < count; i++)
                if (emotes[i] == emoteId)
                    return true;
        }
        return false;
    }
}
