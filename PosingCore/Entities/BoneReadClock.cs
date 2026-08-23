namespace Poser.Entities;

/// <summary>
/// The frame counter behind bone-transform demand. The finalize hook
/// advances it once per render frame; reading a bone's transform stamps the
/// bone with the current value; the hook copies only bones stamped
/// recently. The clock lives HERE so the entity can stamp without the core
/// referencing the game layer.
/// </summary>
public static class BoneReadClock
{
    private static long _frame;

    public static long Frame => System.Threading.Volatile.Read(ref _frame);

    /// <summary>The hook's once-per-frame advance.</summary>
    public static long Advance() =>
        System.Threading.Interlocked.Increment(ref _frame);

    private static long _lastRead;

    /// <summary>The frame of the newest bone-transform read anywhere —
    /// what keeps the snapshot pass itself alive: no reads, no walk.</summary>
    public static long LastRead => System.Threading.Volatile.Read(ref _lastRead);

    internal static void MarkRead() =>
        System.Threading.Volatile.Write(ref _lastRead, Frame);
}
