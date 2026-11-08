using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Game.Core;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>
/// Temporarily disables GPose's recorded motion so it cannot replace an animation
/// chosen in Poser. Saves the actor record, clears its kind, and restores the record
/// when the full-body animation returns to the saved state. Excludes the real player.
/// </summary>
internal sealed unsafe class GPoseMotionRecords
{
    // EventGPoseController.FindActorRecord (patchcheck): the actor's 0xC0 record, or null.
    private delegate byte* FindActorRecordDelegate(nint controller, Character* actor);
    private readonly FindActorRecordDelegate? _find;

    private const int RecordSize = NativeFields.GPoseActorRecord_Size;
    private const int KindOffset = NativeFields.GPoseActorRecord_Kind;    // patchcheck GPoseActorRecord.Kind: 1 emote, 2 action, 3 other
    private const int ActorOffset = NativeFields.GPoseActorRecord_Actor;   // patchcheck GPoseActorRecord.Actor
    private const int IdOffset = NativeFields.GPoseActorRecord_Id;         // patchcheck GPoseActorRecord.Id: the recorded motion

    /// <summary>Saved GPose record and the full-body animation state at capture.</summary>
    private sealed record Taken(byte[] Record, uint EntityId, CharacterModes Mode, byte Param,
        ushort BaseOverride, ushort SlotRow);

    private readonly Dictionary<nint, Taken> _taken = new();

    public GPoseMotionRecords(ISigScanner scanner, IPluginLog log)
    {
        try
        {
            if (scanner.TryScanText(Poser.Game.Natives.NativeSignatures.EventGPoseController_FindActorRecord, out var find)
                && find != 0)
                _find = Marshal.GetDelegateForFunctionPointer<FindActorRecordDelegate>(find);
            else
                log.Warning("Animation: the GPose motion record lookup was not found; a recorded GPose emote can replace Poser's.");
        }
        catch (Exception ex)
        {
            log.Warning($"Animation: the GPose motion record lookup failed ({ex.Message}).");
        }
    }

    private byte* Record(Character* character)
    {
        nint controller = GPoseObjectTable.ControllerAddress();
        if (_find == null || controller == 0)
            return null;
        byte* record = _find(controller, character);
        return record != null && *(Character**)(record + ActorOffset) == character ? record : null;
    }

    /// <summary>Stops GPose replaying the actor's recorded motion before Poser's
    /// first write to its Full body. The live record decides: a kind set means GPose
    /// recorded a motion since (a new session reuses actor addresses and ids), so it
    /// is taken again and its snapshot replaces any older one.</summary>
    internal void TakeOver(Character* character)
    {
        if (AnimationNativeState.IsRealPlayer(character))
            return;
        byte* record = Record(character);
        if (record == null || record[KindOffset] == 0)
            return;
        _taken[(nint)character] = new Taken(new ReadOnlySpan<byte>(record, RecordSize).ToArray(),
            character->GameObject.EntityId, character->Mode, character->ModeParam,
            character->Timeline.BaseOverride, character->Timeline.TimelineSequencer.TimelineIds[0]);
        record[KindOffset] = 0;
    }

    /// <summary>Gives the record back when the Full body returns to the state Poser
    /// took it in (a reset, not a layer reset's own snapshot of Poser's state).</summary>
    internal void GiveBack(Character* character, CharacterModes mode, byte param, ushort baseOverride, ushort slotRow)
    {
        if (!_taken.TryGetValue((nint)character, out var taken))
            return;
        if (taken.EntityId != character->GameObject.EntityId)
        {
            _taken.Remove((nint)character);
            return;
        }
        if (taken.Mode != mode || taken.Param != param || taken.BaseOverride != baseOverride || taken.SlotRow != slotRow)
            return;
        _taken.Remove((nint)character);
        // Only over the record Poser cleared: its kind still 0 and the same recorded
        // motion. A newer recording sets a kind; a changed motion id also prevents
        // restoration. These checks cannot distinguish sessions that reuse the same id. The game's own per-frame
        // writes to the record (its countdown and flags) don't count.
        // A snapshot without a motion id (kind 3 can carry 0) can't tell its record
        // from an empty one, so it is never copied back.
        uint takenId = BitConverter.ToUInt32(taken.Record, IdOffset);
        byte* record = Record(character);
        if (record != null && takenId != 0 && record[KindOffset] == 0 && *(uint*)(record + IdOffset) == takenId)
            taken.Record.CopyTo(new Span<byte>(record, RecordSize));
    }
}
