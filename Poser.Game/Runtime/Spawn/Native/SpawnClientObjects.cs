namespace Poser.Game;

/// <summary>
/// One client object as ClientObjectManager reports it. It carries BOTH
/// numbers the native object has — the slot it was resolved by and its global
/// <see cref="ObjectIndex"/> — because they genuinely differ (see
/// <c>docs/architecture/posing-runtime.md</c>), so a test ClientObjectManager
/// reproduces the difference and picking the wrong one fails a test instead of
/// a live spawn.
/// </summary>
internal readonly record struct ClientObjectSnapshot(
    nint Address,
    ulong EntityId,
    ushort ObjectIndex);

/// <summary>
/// The four ClientObjectManager entry points the spawn transaction needs.
/// <see cref="CreateBattleCharacter"/> is declared WITHOUT default arguments on
/// purpose: the native signature is
/// <c>CreateBattleCharacter(uint index = uint.MaxValue, byte param = 0)</c>,
/// where a lone <c>byte</c> binds silently to <c>index</c> — the slot to build
/// in — instead of the companion flag. Both arguments are mandatory here, so
/// that mistake cannot compile.
/// </summary>
internal interface IClientObjectManagerNative
{
    uint CreateBattleCharacter(uint index, byte param);
    ClientObjectSnapshot? GetObjectByIndex(ushort index);
    uint GetIndexByObject(nint address);
    void DeleteObjectByIndex(ushort index, byte param);
}

/// <summary>
/// Client-object identity and lifetime bookkeeping: the production logic
/// behind the adapter's index members, kept off <c>unsafe</c> so tests run it
/// against a ClientObjectManager whose two index spaces really differ.
/// </summary>
internal sealed class SpawnClientObjects
{
    public const uint NoIndex = 0xFFFFFFFF;

    /// <summary>Let the game pick the slot. Naming one is how the clone ended
    /// up aimed at slot 0 — the GPose primary.</summary>
    private const uint NextAvailableSlot = uint.MaxValue;

    private readonly IClientObjectManagerNative _com;

    public SpawnClientObjects(IClientObjectManagerNative com) => _com = com;

    /// <summary>Destruction bookkeeping; the real adapter feeds it from the
    /// Character finalize hook.</summary>
    public SpawnLifetimeStamps Stamps { get; } = new();

    public uint CreateBattleCharacter(byte reserveCompanionSlot) =>
        _com.CreateBattleCharacter(NextAvailableSlot, reserveCompanionSlot);

    public ulong IndexDestructionStamp(ushort index) => Stamps.IndexStampFor(index);

    public SpawnNativeDescriptor? ResolveByIndex(ushort index)
    {
        if (_com.GetObjectByIndex(index) is not { } native)
            return null;
        return new SpawnNativeDescriptor(
            index,
            native.Address,
            native.EntityId,
            Stamps.StampFor(native.Address));
    }

    public SpawnNativeDescriptor? ResolveActor(nint address)
    {
        if (address == nint.Zero)
            return null;
        var index = _com.GetIndexByObject(address);
        if (index == NoIndex)
            return null;
        var current = ResolveByIndex((ushort)index);
        return current is { } descriptor && descriptor.Address == address
            ? descriptor
            : null;
    }

    public bool DeleteExact(SpawnNativeDescriptor descriptor)
    {
        var current = ResolveByIndex(descriptor.Index);
        if (current is null || current.Value != descriptor)
            return false;
        _com.DeleteObjectByIndex(descriptor.Index, 0);
        return true;
    }
}
