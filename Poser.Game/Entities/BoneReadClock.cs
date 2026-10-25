namespace Poser.Game.Entities;

/// <summary>
/// Tracks render frames and the most recent bone-transform read. The finalize
/// hook uses these stamps to skip snapshot work when no consumer needs it.
/// </summary>
public static class BoneReadClock
{
    private static long _frame;

    public static long Frame => System.Threading.Volatile.Read(ref _frame);

    /// <summary>Advances the counter once per render frame.</summary>
    public static long Advance() =>
        System.Threading.Interlocked.Increment(ref _frame);

    private static long _lastRead;

    /// <summary>The frame containing the newest bone-transform read.</summary>
    public static long LastRead => System.Threading.Volatile.Read(ref _lastRead);

    internal static void MarkRead() =>
        System.Threading.Volatile.Write(ref _lastRead, Frame);
}
