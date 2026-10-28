using System.Threading;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// Demand signal for the per-frame bone snapshot. Readers request a snapshot
/// while visible, allowing the finalize hook to skip the full skeleton walk
/// when neither UI nor camera tracking has read a bone recently.
/// </summary>
public static class BoneSnapshotDemand
{
    /// <summary>How many render frames a request stays fresh. Two frames
    /// bridges the draw-then-finalize ordering without keeping the walk
    /// alive after the last reader closes.</summary>
    private const int FreshFrames = 2;

    private static long _lastRequestFrame;

    /// <summary>Announces demand before the caller's first bone read.</summary>
    public static void Request() =>
        Interlocked.Exchange(ref _lastRequestFrame, BoneReadClock.Frame);

    /// <summary>The finalize hook advances the shared clock and asks whether
    /// anything read a bone recently — an explicit request or any transform
    /// getter anywhere, so camera tracking with the UI hidden still counts.</summary>
    public static bool Wanted()
    {
        long frame = BoneReadClock.Advance();
        if (frame - BoneReadClock.LastRead <= FreshFrames)
            return true;
        return frame - Interlocked.Read(ref _lastRequestFrame) <= FreshFrames;
    }
}
