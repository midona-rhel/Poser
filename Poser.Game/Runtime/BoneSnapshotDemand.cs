using System.Threading;
using Poser.Entities;

namespace Poser.Game;

/// <summary>
/// Demand signal for the per-frame bone snapshot. The finalize hook copies
/// every bone of every tracked skeleton into managed state each frame — a
/// third of a core in the profile — and the ONLY readers are UI surfaces:
/// the bone overlay, the inspector rail, the matrix. Each of those calls
/// <see cref="Request"/> as it draws; the hook snapshots only while a
/// request is fresh, so a hidden UI costs nothing and an open one pays
/// exactly what it reads.
/// </summary>
public static class BoneSnapshotDemand
{
    /// <summary>How many render frames a request stays fresh. Two frames
    /// bridges the draw-then-finalize ordering without keeping the walk
    /// alive after the last reader closes.</summary>
    private const int FreshFrames = 2;

    private static long _lastRequestFrame;

    /// <summary>A reader is about to need bone transforms this frame. Mostly
    /// redundant now — reading any bone's transform is itself the request —
    /// but a surface may announce itself a frame before its first read.</summary>
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
