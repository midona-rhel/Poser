using Poser.Domain.Companions;

namespace Poser.Game;

/// <summary>
/// The sole native boundary for spawn ownership. Every dereference primitive
/// revalidates the exact descriptor immediately before touching memory and
/// refuses on any mismatch — unresolved identity is never permission.
/// </summary>
internal interface IActorSpawnNativeAdapter
{
    bool IsAvailable { get; }

    /// <summary>True while the Character finalize hook is installed. Without
    /// it no authority may span frames: spawning and delayed callbacks
    /// refuse (fail-closed narrowing).</summary>
    bool IsLifetimeAuthoritative { get; }
    string? LifetimeAuthorityDetail { get; }

    uint CreateBattleCharacter(byte reserveCompanionSlot);
    ulong IndexDestructionStamp(ushort index);
    SpawnNativeDescriptor? ResolveByIndex(ushort index);
    SpawnNativeDescriptor? ResolveActor(nint address);
    bool DeleteExact(SpawnNativeDescriptor descriptor);

    bool EnableDraw(SpawnNativeDescriptor descriptor);

    /// <summary>Writes the character's alpha. This is how an actor is HIDDEN;
    /// see <see cref="ActorSpawnNativeAdapter.SetAlpha"/> for why it is not
    /// a draw-state write.</summary>
    bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha);

    bool? IsReadyToDraw(SpawnNativeDescriptor descriptor);

    /// <summary>Copies the equipment visibility flags — weapons, headgear,
    /// visor, Viera ears — from one character onto another. The seed copy
    /// carries the equipment but not these flags: a duplicate showed the
    /// weapon on its back while the source hid it (2026-09-02).</summary>
    bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target);

    /// <summary>Seeds the target's DrawData customize and equipment from
    /// the source's DRAWN model (Human.Customize, Human equipment models).
    /// A sync plugin or a locked Glamourer state writes the draw object and
    /// leaves DrawData at the game's values, so the game's own copy drew a
    /// vanilla character next to a modded one (2026-09-02, Valya).</summary>
    bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target);
    bool HasCompanionSlot(SpawnNativeDescriptor descriptor);
    /// <summary>Reads the slot. False when the descriptor no longer
    /// revalidates — an unreadable actor is NOT an empty slot, and only the
    /// empty slot may be written over. On true, a null
    /// <paramref name="attachment"/> is the empty slot.</summary>
    bool TryReadCompanion(
        SpawnNativeDescriptor descriptor,
        out CompanionAttachment? attachment);

    /// <summary>The attached child object's address; zero when the slot is
    /// empty or the descriptor no longer revalidates. It is the companion's
    /// own BODY — the attachment ids alone name a sheet row, not a posable
    /// object.</summary>
    nint ReadCompanionAddress(SpawnNativeDescriptor descriptor);

    bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id);
    bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want);
    bool EnableCompanionDraw(SpawnNativeDescriptor descriptor);
    int? ReadModelCharaId(SpawnNativeDescriptor descriptor);
    bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId);
}
