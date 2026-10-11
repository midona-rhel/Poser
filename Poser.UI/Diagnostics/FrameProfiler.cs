using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;

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
    private readonly FrameProfiler? _profiler;

    internal ProfileScope(FrameProfiler profiler) => _profiler = profiler;

    public void Dispose() => _profiler?.Close();
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
/// <para>OFF IS FREE: <see cref="Enabled"/> is a plain field read before
/// anything else happens, and a disabled <see cref="ProfileScope"/> carries
/// no profiler, so a disabled scope is a predictable branch and no allocation
/// at all. ON is allocation-free too once a label has been seen once: label
/// slots are interned on first sight and the per-frame accumulators are
/// preallocated arrays indexed by slot.</para>
///
/// <para>The switch is applied at the FRAME BOUNDARY, never mid-frame:
/// flipping it between a scope's open and its close would unbalance the
/// nesting stack. <see cref="SetEnabled"/> therefore records a request that
/// <see cref="BeginFrame"/> honours.</para>
///
/// <para>One ledger per plugin load: the UI root frames it, the measured
/// surfaces open scopes on it and the PERF panel reads it.</para>
/// </summary>
public sealed class FrameProfiler
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

    private readonly IPluginLog _log;

    public FrameProfiler(IPluginLog log) => _log = log;

    /// <summary>Whether scopes record — the "near-zero when off" gate, read
    /// once at the head of every scope. Written only at a frame boundary; see
    /// <see cref="SetEnabled"/>.</summary>
    public bool Enabled { get; private set; }

    private bool _pendingEnabled;

    private static long Now() => Stopwatch.GetTimestamp();

    // ── label slots (interned once, never per frame) ─────────────────────
    private readonly Dictionary<string, int> Slots =
        new(64, StringComparer.Ordinal);
    private string[] _labels = new string[64];
    private long[] _frameSelfTicks = new long[64];
    private long[] _frameInclusiveTicks = new long[64];
    private int[] _frameHits = new int[64];
    private double[] _averageSelfMs = new double[64];
    private double[] _averageInclusiveMs = new double[64];
    private double[] _peakSelfMs = new double[64];
    private int[] _lastHits = new int[64];
    private bool[] _seeded = new bool[64];
    private int _count;

    // ── the open-scope stack ─────────────────────────────────────────────
    private readonly int[] StackSlot = new int[MaximumDepth];
    private readonly long[] StackStart = new long[MaximumDepth];
    private readonly long[] StackChild = new long[MaximumDepth];
    private int _depth;

    private long _frameStart;
    private bool _frameOpen;
    private bool _frameSeeded;

    /// <summary>The whole draw callback's CPU cost, last frame.</summary>
    public double LastFrameMs { get; private set; }

    /// <summary>The whole draw callback's CPU cost, smoothed.</summary>
    public double AverageFrameMs { get; private set; }

    /// <summary>The worst whole-callback frame since the last peak reset.
    /// </summary>
    public double PeakFrameMs { get; private set; }

    /// <summary>How many distinct labels have been seen this session.
    /// </summary>
    public int LabelCount => _count;

    /// <summary>Requests the recording state. Honoured at the next
    /// <see cref="BeginFrame"/>, because the nesting stack cannot survive a
    /// mid-frame flip.</summary>
    public void SetEnabled(bool enabled) => _pendingEnabled = enabled;

    /// <summary>The requested state, whether or not a frame boundary has
    /// applied it yet.</summary>
    public bool Requested => _pendingEnabled;

    /// <summary>Opens the frame. Idempotent per frame by construction — the
    /// UI root calls it exactly once, before any window draws.</summary>
    public void BeginFrame()
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
    /// <summary>Frames past this log their costliest units. The bar is the
    /// 120fps budget: the WHOLE game gets 8.3ms, so a Poser draw pass past
    /// 4ms is already guaranteed to be the reason a frame dropped.</summary>
    private const double HitchThresholdMs = 4.0;

    /// <summary>A sustained overshoot logs once a second, not once a frame.
    /// </summary>
    private long _lastHitchLogTicks;

    public void EndFrame()
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

        // A hitch attributes itself: a frame past the threshold logs its
        // costliest units before the per-slot counters reset, so a periodic
        // spike names its owner in the log without anybody watching the
        // panel when it fires.
        if (frameMs > HitchThresholdMs &&
            (Now() - _lastHitchLogTicks) * MillisecondsPerTick > 1000.0)
        {
            _lastHitchLogTicks = Now();
            var lines = new System.Text.StringBuilder();
            lines.Append(
                $"Frame hitch {frameMs:0}ms — top units:");
            for (int reported = 0; reported < 5; reported++)
            {
                int best = -1;
                long bestTicks = 0;
                for (int slot = 0; slot < _count; slot++)
                {
                    if (_frameSelfTicks[slot] > bestTicks)
                    {
                        bestTicks = _frameSelfTicks[slot];
                        best = slot;
                    }
                }
                if (best < 0 || bestTicks <= 0)
                    break;
                lines.Append(
                    $" [{_labels[best]}] " +
                    $"{bestTicks * MillisecondsPerTick:0.0}ms" +
                    $" x{_frameHits[best]};");
                _frameSelfTicks[best] = -_frameSelfTicks[best];
            }
            for (int slot = 0; slot < _count; slot++)
                if (_frameSelfTicks[slot] < 0)
                    _frameSelfTicks[slot] = -_frameSelfTicks[slot];
            _log.Debug(lines.ToString());
        }

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
    public ProfileScope Scope(string label)
    {
        if (!Enabled || _depth >= MaximumDepth)
            return default;
        int slot = SlotFor(label);
        StackSlot[_depth] = slot;
        StackChild[_depth] = 0L;
        StackStart[_depth] = Now();
        _depth++;
        return new ProfileScope(this);
    }

    internal void Close()
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

    private int SlotFor(string label)
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

    private void Grow()
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
    public int Snapshot(Sample[] destination)
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
    public void ResetPeaks()
    {
        PeakFrameMs = 0.0;
        for (int slot = 0; slot < _count; slot++)
            _peakSelfMs[slot] = 0.0;
    }

    /// <summary>Drops every label and every figure. The label slots stay
    /// allocated; they are the pool.</summary>
    public void Reset()
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
