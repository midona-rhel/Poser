using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Poser.UI;

/// <summary>
/// One draw unit's measured span. Handed out by
/// <see cref="FrameProfiler.Scope"/> and closed by <c>using</c>.
///
/// <para>A <c>ref struct</c> deliberately: it cannot be boxed, stored in a
/// field, or captured by a closure, so a scope can only ever live and die
/// inside the block that opened it — which is the whole of the nesting
/// contract the aggregation below depends on.</para>
/// </summary>
public readonly ref struct ProfileScope
{
    private readonly bool _recording;

    internal ProfileScope(bool recording) => _recording = recording;

    public void Dispose()
    {
        if (_recording)
            FrameProfiler.Close();
    }
}

/// <summary>
/// THE draw-cost ledger. Every major draw unit wears a
/// <see cref="Scope"/>; each frame the profiler folds the measured spans
/// into a per-label exponential average and a per-label peak, which the
/// PERF panel reads.
///
/// <para>WHAT IT MEASURES: CPU time spent inside the plugin's own draw
/// callback, nothing else. The GPU work an ImGui draw list causes — the
/// backdrop blur above all — is submitted here and executed later, so it is
/// invisible to every number this class produces. The panel says so in its
/// own footnote, and any reading of these figures has to carry that caveat
/// with it.</para>
///
/// <para>SELF vs INCLUSIVE: scopes nest (window → column → section), so
/// inclusive time double-counts down the tree and the outermost label always
/// wins. Each scope therefore also reports SELF time — its inclusive span
/// minus the inclusive spans of the scopes opened directly inside it — and
/// that is what the panel sorts on, because "what costs" is a question about
/// the work a unit does itself.</para>
///
/// <para>OFF IS FREE: <see cref="Enabled"/> is a plain static field read
/// before anything else happens, and <see cref="ProfileScope"/> is an empty
/// struct, so a disabled scope is a predictable branch and no allocation at
/// all. ON is allocation-free too once a label has been seen once: label
/// slots are interned on first sight and the per-frame accumulators are
/// preallocated arrays indexed by slot. Both claims are pinned by
/// FrameProfilerContractTests.</para>
///
/// <para>The switch is applied at the FRAME BOUNDARY, never mid-frame:
/// flipping it between a scope's open and its close would unbalance the
/// nesting stack. <see cref="SetEnabled"/> therefore records a request that
/// <see cref="BeginFrame"/> honours.</para>
/// </summary>
public static class FrameProfiler
{
    /// <summary>The EMA weight given to the newest frame. 0.05 is roughly a
    /// 20-frame window — slow enough to read while the pointer moves, fast
    /// enough that opening a pane shows up at once.</summary>
    public const double Smoothing = 0.05;

    /// <summary>Nesting depth beyond which scopes stop recording rather than
    /// growing a stack. Nothing in the shell nests anywhere near this; the
    /// cap exists so a runaway recursion degrades instead of allocating.
    /// </summary>
    private const int MaximumDepth = 64;

    internal static readonly double MillisecondsPerTick =
        1000.0 / Stopwatch.Frequency;

    /// <summary>Whether scopes record — the "near-zero when off" gate, read
    /// once at the head of every scope. Written only at a frame boundary; see
    /// <see cref="SetEnabled"/>.</summary>
    public static bool Enabled { get; private set; }

    private static bool _pendingEnabled;

    /// <summary>
    /// The clock, pinned. Null in every real run, so the reader below is a
    /// predicted branch onto <see cref="Stopwatch.GetTimestamp"/>; the
    /// profiler's own contract tests set it so the aggregation arithmetic can
    /// be asserted on exact tick counts instead of on wall-clock noise.
    /// </summary>
    internal static long? ManualClock;

    private static long Now() => ManualClock ?? Stopwatch.GetTimestamp();

    // ── label slots (interned once, never per frame) ─────────────────────
    private static readonly Dictionary<string, int> Slots =
        new(64, StringComparer.Ordinal);
    private static string[] _labels = new string[64];
    private static long[] _frameSelfTicks = new long[64];
    private static long[] _frameInclusiveTicks = new long[64];
    private static int[] _frameHits = new int[64];
    private static double[] _averageSelfMs = new double[64];
    private static double[] _averageInclusiveMs = new double[64];
    private static double[] _peakSelfMs = new double[64];
    private static int[] _lastHits = new int[64];
    private static bool[] _seeded = new bool[64];
    private static int _count;

    // ── the open-scope stack ─────────────────────────────────────────────
    private static readonly int[] StackSlot = new int[MaximumDepth];
    private static readonly long[] StackStart = new long[MaximumDepth];
    private static readonly long[] StackChild = new long[MaximumDepth];
    private static int _depth;

    private static long _frameStart;
    private static bool _frameOpen;
    private static bool _frameSeeded;

    /// <summary>The whole draw callback's CPU cost, last frame.</summary>
    public static double LastFrameMs { get; private set; }

    /// <summary>The whole draw callback's CPU cost, smoothed.</summary>
    public static double AverageFrameMs { get; private set; }

    /// <summary>The worst whole-callback frame since the last peak reset.
    /// </summary>
    public static double PeakFrameMs { get; private set; }

    /// <summary>How many distinct labels have been seen this session.
    /// </summary>
    public static int LabelCount => _count;

    /// <summary>Requests the recording state. Honoured at the next
    /// <see cref="BeginFrame"/>, because the nesting stack cannot survive a
    /// mid-frame flip.</summary>
    public static void SetEnabled(bool enabled) => _pendingEnabled = enabled;

    /// <summary>The requested state, whether or not a frame boundary has
    /// applied it yet.</summary>
    public static bool Requested => _pendingEnabled;

    /// <summary>Opens the frame. Idempotent per frame by construction — the
    /// UI root calls it exactly once, before any window draws.</summary>
    public static void BeginFrame()
    {
        // The stack is reset rather than asserted: a draw that threw past a
        // `using` still unwound its scope, but a scope opened at MaximumDepth
        // recorded nothing and a partially-drawn frame must not poison the
        // next one.
        _depth = 0;
        Enabled = _pendingEnabled;
        if (!Enabled)
        {
            _frameOpen = false;
            return;
        }
        _frameOpen = true;
        _frameStart = Now();
    }

    /// <summary>Closes the frame and folds every label's measured span into
    /// its average and peak. A label NOT seen this frame samples zero, so a
    /// closed window's row decays instead of standing at its last value
    /// forever.</summary>
    public static void EndFrame()
    {
        if (!_frameOpen)
            return;
        _frameOpen = false;

        double frameMs = (Now() - _frameStart)
            * MillisecondsPerTick;
        LastFrameMs = frameMs;
        AverageFrameMs = _frameSeeded
            ? AverageFrameMs + Smoothing * (frameMs - AverageFrameMs)
            : frameMs;
        _frameSeeded = true;
        if (frameMs > PeakFrameMs)
            PeakFrameMs = frameMs;

        for (int slot = 0; slot < _count; slot++)
        {
            double selfMs = _frameSelfTicks[slot] * MillisecondsPerTick;
            double inclusiveMs =
                _frameInclusiveTicks[slot] * MillisecondsPerTick;

            _averageSelfMs[slot] = _seeded[slot]
                ? _averageSelfMs[slot]
                    + Smoothing * (selfMs - _averageSelfMs[slot])
                : selfMs;
            _averageInclusiveMs[slot] = _seeded[slot]
                ? _averageInclusiveMs[slot]
                    + Smoothing * (inclusiveMs - _averageInclusiveMs[slot])
                : inclusiveMs;
            _seeded[slot] = true;

            if (selfMs > _peakSelfMs[slot])
                _peakSelfMs[slot] = selfMs;

            _lastHits[slot] = _frameHits[slot];
            _frameSelfTicks[slot] = 0;
            _frameInclusiveTicks[slot] = 0;
            _frameHits[slot] = 0;
        }
    }

    /// <summary>
    /// Opens a measured span for <paramref name="label"/>. The label must be
    /// a constant — a per-frame built string would defeat the interned slot
    /// table and allocate on every frame.
    /// </summary>
    public static ProfileScope Scope(string label)
    {
        if (!Enabled || _depth >= MaximumDepth)
            return default;
        int slot = SlotFor(label);
        StackSlot[_depth] = slot;
        StackChild[_depth] = 0L;
        StackStart[_depth] = Now();
        _depth++;
        return new ProfileScope(true);
    }

    internal static void Close()
    {
        long now = Now();
        // A scope that recorded cannot close below zero: ProfileScope only
        // carries `recording: true` when the push succeeded.
        _depth--;
        long inclusive = now - StackStart[_depth];
        long self = inclusive - StackChild[_depth];
        int slot = StackSlot[_depth];
        _frameInclusiveTicks[slot] += inclusive;
        _frameSelfTicks[slot] += self;
        _frameHits[slot]++;
        if (_depth > 0)
            StackChild[_depth - 1] += inclusive;
    }

    private static int SlotFor(string label)
    {
        if (Slots.TryGetValue(label, out int slot))
            return slot;
        if (_count == _labels.Length)
            Grow();
        slot = _count++;
        _labels[slot] = label;
        Slots[label] = slot;
        return slot;
    }

    private static void Grow()
    {
        int size = _labels.Length * 2;
        Array.Resize(ref _labels, size);
        Array.Resize(ref _frameSelfTicks, size);
        Array.Resize(ref _frameInclusiveTicks, size);
        Array.Resize(ref _frameHits, size);
        Array.Resize(ref _averageSelfMs, size);
        Array.Resize(ref _averageInclusiveMs, size);
        Array.Resize(ref _peakSelfMs, size);
        Array.Resize(ref _lastHits, size);
        Array.Resize(ref _seeded, size);
    }

    /// <summary>One label's published figures.</summary>
    public readonly record struct Sample(
        string Label,
        double AverageSelfMs,
        double PeakSelfMs,
        double AverageInclusiveMs,
        int Hits);

    /// <summary>
    /// Copies the published figures into <paramref name="destination"/> and
    /// returns how many were written. The caller owns the buffer and reuses
    /// it, so reading the ledger allocates nothing; a buffer shorter than
    /// <see cref="LabelCount"/> is filled and the rest dropped.
    /// </summary>
    public static int Snapshot(Sample[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int written = Math.Min(destination.Length, _count);
        for (int slot = 0; slot < written; slot++)
            destination[slot] = new Sample(
                _labels[slot],
                _averageSelfMs[slot],
                _peakSelfMs[slot],
                _averageInclusiveMs[slot],
                _lastHits[slot]);
        return written;
    }

    /// <summary>Clears the peaks — both the per-label ones and the frame's —
    /// leaving the averages running.</summary>
    public static void ResetPeaks()
    {
        PeakFrameMs = 0.0;
        for (int slot = 0; slot < _count; slot++)
            _peakSelfMs[slot] = 0.0;
    }

    /// <summary>Drops every label and every figure. The label slots stay
    /// allocated; they are the pool.</summary>
    public static void Reset()
    {
        Slots.Clear();
        Array.Clear(_labels);
        Array.Clear(_frameSelfTicks);
        Array.Clear(_frameInclusiveTicks);
        Array.Clear(_frameHits);
        Array.Clear(_averageSelfMs);
        Array.Clear(_averageInclusiveMs);
        Array.Clear(_peakSelfMs);
        Array.Clear(_lastHits);
        Array.Clear(_seeded);
        _count = 0;
        _depth = 0;
        _frameOpen = false;
        _frameSeeded = false;
        LastFrameMs = 0.0;
        AverageFrameMs = 0.0;
        PeakFrameMs = 0.0;
    }
}
