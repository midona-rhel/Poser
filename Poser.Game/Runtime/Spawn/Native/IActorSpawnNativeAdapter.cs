using Poser.Domain.Companions;

namespace Poser.Game;

/// <summary>
/// The only code that reads or writes actors Poser spawned in game memory.
/// Every call first checks that the actor is still the same one, and does
/// nothing if it is not.
/// </summary>
internal interface IActorSpawnNativeAdapter
{
    bool IsAvailable { get; }

    /// <summary>True while Poser is told each time the game destroys a
    /// character. Without that, Poser cannot tell whether an actor it saw last
    /// frame is still the same one, so spawning and delayed work do nothing.</summary>
    bool IsLifetimeAuthoritative { get; }
    string? LifetimeAuthorityDetail { get; }

    uint CreateBattleCharacter(byte reserveCompanionSlot);
    ulong IndexDestructionStamp(ushort index);
    SpawnNativeDescriptor? ResolveByIndex(ushort index);
    SpawnNativeDescriptor? ResolveActor(nint address);
    bool DeleteExact(SpawnNativeDescriptor descriptor);

    bool EnableDraw(SpawnNativeDescriptor descriptor);

    /// <summary>Sets how see-through the actor is. Poser hides an actor this
    /// way because turning its drawing off would throw away its body and its
    /// pose.</summary>
    bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha);

    bool? IsReadyToDraw(SpawnNativeDescriptor descriptor);

    /// <summary>Copies the equipment visibility flags — weapons, headgear,
    /// visor, Viera ears — from one character onto another. Copying an actor
    /// copies its equipment but not these flags, so without this a copy would
    /// show a weapon the original hides.</summary>
    bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target);

    /// <summary>Gives the copy the appearance and equipment the original
    /// actor shows on screen. A sync plugin or a locked Glamourer state changes
    /// only what is drawn, not the game's own record of the actor, so copying
    /// that record alone would give an unmodded copy of a modded actor.</summary>
    bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target);
    bool HasCompanionSlot(SpawnNativeDescriptor descriptor);
    /// <summary>Reads the actor's companion, mount or ornament. Returns false
    /// when the actor is no longer the same one. An actor that cannot be read
    /// is not treated as having an empty slot, because only an empty slot may
    /// be overwritten. On true, a null <paramref name="attachment"/> means the
    /// slot is empty.</summary>
    bool TryReadCompanion(
        SpawnNativeDescriptor descriptor,
        out CompanionAttachment? attachment);

    /// <summary>Where the companion's own body is in game memory; zero when
    /// there is none or the actor is no longer the same one. The companion's
    /// ids only say which kind it is; posing it needs its body.</summary>
    nint ReadCompanionAddress(SpawnNativeDescriptor descriptor);

    bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id);
    bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want);
    bool EnableCompanionDraw(SpawnNativeDescriptor descriptor);
    int? ReadModelCharaId(SpawnNativeDescriptor descriptor);
    bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId);
}
