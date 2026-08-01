using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// The ONE keyed animation store. Components own no transition
/// dictionaries: they hand Motion the stable ImGui identity of the thing
/// being animated and read the current value back, so keying, the
/// per-frame advance and eviction all live in one place.
///
/// Two models exist because the components genuinely need two:
/// <list type="bullet">
/// <item><b>Constant-rate</b> (<see cref="Progress"/>) — a symmetric 0..1
/// ramp, forward while a condition holds and backward while it does not.
/// A hover that reverses mid-flight retraces exactly the distance it
/// already covered instead of replaying a full duration.</item>
/// <item><b>Elapsed-clock</b> (<see cref="Toward"/>) — a retargeting
/// clock: every target change captures the current value as the new
/// origin and replays a full duration from it. All channels under one
/// identity share ONE clock, so a multi-channel state (background +
/// opacity) restarts as one element when any single channel retargets,
/// the way a CSS transition list on one element does.</item>
/// </list>
/// Both stores hold exactly ONE record per identity, so a group cannot
/// desynchronise against itself and eviction counts controls, not
/// channels.
/// </summary>
internal static class Motion
{
    // Eviction heuristic, never a rendering rule: sweep only once a store
    // is genuinely large and only when a miss proves the caller set is
    // churning. The one observable consequence is that an identity gone
    // for more than StaleFrames snaps to its targets when it returns.
    private const int PruneThreshold = 512;
    private const int StaleFrames = 2;

    private abstract class Entry
    {
        public int LastFrame;
    }

    private sealed class RampEntry : Entry
    {
        public float Progress;
    }

    /// <summary>One channel of a group: its id plus the origin, target
    /// and current value it interpolates.</summary>
    private struct Lane
    {
        public int Channel;
        public Vector4 From;
        public Vector4 Target;
        public Vector4 Value;
    }

    private sealed class GroupEntry : Entry
    {
        public float Elapsed;
        public Lane[] Lanes = [];
    }

    private static readonly Dictionary<uint, RampEntry> Ramps = new();
    private static readonly Dictionary<uint, GroupEntry> Groups = new();

    // Reused across prunes: the sweep cannot remove while enumerating.
    private static readonly List<uint> Stale = new();

    /// <summary>
    /// Constant-rate linear progress for <paramref name="id"/>: 0..1
    /// covered in <paramref name="durationSeconds"/>, forward while
    /// <paramref name="on"/> and backward otherwise. Returns the RAW
    /// progress — the caller applies its own easing, because the same
    /// ramp can drive several eased properties. First sight seeds at the
    /// resting end of the requested state so a control that appears
    /// already hovered does not fade in.
    /// </summary>
    internal static float Progress(
        uint id, bool on, float durationSeconds)
    {
        int frame = ImGui.GetFrameCount();
        if (!Ramps.TryGetValue(id, out var entry))
        {
            if (Ramps.Count > PruneThreshold)
                Prune(Ramps, frame);
            entry = new RampEntry { Progress = on ? 1f : 0f };
            Ramps[id] = entry;
        }
        float step = durationSeconds > 0f
            ? ImGui.GetIO().DeltaTime / durationSeconds
            : 1f;
        entry.Progress = Math.Clamp(
            entry.Progress + (on ? step : -step), 0f, 1f);
        entry.LastFrame = frame;
        return entry.Progress;
    }

    /// <summary>
    /// Elapsed-clock retarget for a whole group of channels under one
    /// identity: the caller writes each channel's TARGET into
    /// <paramref name="channels"/> and Motion writes the CURRENT value
    /// back into the same slots.
    ///
    /// Every target is inspected before any value moves, because the
    /// clock is shared: if any channel retargets, all of them capture
    /// their current value as the new origin and the group replays a full
    /// duration together. That is why targets arrive as one span instead
    /// of one call per channel — a channel resolved before its siblings'
    /// targets were known could not tell an advance from a restart.
    ///
    /// A newly seen identity, one drawn twice in the same frame (its
    /// clock cannot be trusted), and a zero-duration transition all snap
    /// straight onto their targets, so controls appear in — and arrive
    /// at — their resting state instead of freezing part-way.
    ///
    /// An identity must be handed the SAME channel set, in the same
    /// order, on every call, and never the same channel twice: the clock
    /// only stays shared while every channel advances together.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call repeats a
    /// channel, or its channel set differs from the one already stored
    /// for <paramref name="id"/>.</exception>
    internal static void Toward(
        uint id, in Transition transition, Span<MotionChannel> channels)
    {
        // O(channels²) over a set of two, allocation-free: only the
        // message allocates, and only on the way out.
        for (int i = 1; i < channels.Length; i++)
            for (int j = 0; j < i; j++)
                if (channels[i].Channel == channels[j].Channel)
                    throw Contract(id, $"lists channel "
                        + $"{channels[i].Channel} at slots {j} and {i}");

        int frame = ImGui.GetFrameCount();
        if (!Groups.TryGetValue(id, out var entry))
        {
            if (Groups.Count > PruneThreshold)
                Prune(Groups, frame);
            entry = new GroupEntry { Lanes = new Lane[channels.Length] };
            Groups[id] = entry;
            Seed(entry, channels, transition, frame);
            return;
        }
        if (entry.Lanes.Length != channels.Length)
            throw Contract(id, $"carries {channels.Length} channels where "
                + $"the stored group holds {entry.Lanes.Length}, first "
                + $"unmatched channel {(channels.Length > entry.Lanes.Length
                    ? channels[entry.Lanes.Length].Channel
                    : entry.Lanes[channels.Length].Channel)}");
        for (int i = 0; i < channels.Length; i++)
            if (entry.Lanes[i].Channel != channels[i].Channel)
                throw Contract(id, $"has channel {channels[i].Channel} at "
                    + $"slot {i} where the stored group holds channel "
                    + $"{entry.Lanes[i].Channel}");
        // Drawn twice in one frame: reseed rather than advance a clock
        // that two callers are sharing by accident.
        if (frame <= entry.LastFrame)
        {
            Seed(entry, channels, transition, frame);
            return;
        }

        bool retarget = false;
        for (int i = 0; i < channels.Length; i++)
            if (entry.Lanes[i].Target != channels[i].Value)
            {
                retarget = true;
                break;
            }

        float duration = transition.DurationSeconds;
        if (retarget)
        {
            entry.Elapsed = 0f;
            for (int i = 0; i < channels.Length; i++)
            {
                ref var lane = ref entry.Lanes[i];
                lane.From = lane.Value;
                lane.Target = channels[i].Value;
                // No clock to run: the retarget IS the arrival.
                if (duration <= 0f)
                    lane.Value = lane.Target;
            }
        }
        else if (entry.Elapsed < duration)
        {
            entry.Elapsed = MathF.Min(
                duration, entry.Elapsed + ImGui.GetIO().DeltaTime);
            float eased = transition.Evaluate(entry.Elapsed / duration);
            for (int i = 0; i < channels.Length; i++)
            {
                ref var lane = ref entry.Lanes[i];
                lane.Value = channels[i].Premultiplied
                    ? Crystarium.PremultipliedLerp(
                        lane.From, lane.Target, eased)
                    : new Vector4(
                        lane.From.X
                            + (lane.Target.X - lane.From.X) * eased,
                        0f,
                        0f,
                        0f);
            }
        }
        entry.LastFrame = frame;
        for (int i = 0; i < channels.Length; i++)
            channels[i].Value = entry.Lanes[i].Value;
    }

    /// <summary>Snaps every lane onto its target with the clock already
    /// spent. The channels already carry those targets, so the seed path
    /// never needs a write-back.</summary>
    private static void Seed(
        GroupEntry entry,
        Span<MotionChannel> channels,
        in Transition transition,
        int frame)
    {
        for (int i = 0; i < channels.Length; i++)
        {
            var target = channels[i].Value;
            entry.Lanes[i] = new Lane
            {
                Channel = channels[i].Channel,
                From = target,
                Target = target,
                Value = target,
            };
        }
        entry.Elapsed = transition.DurationSeconds;
        entry.LastFrame = frame;
    }

    // Built only on the failing path, so the audit above stays free.
    private static InvalidOperationException Contract(
        uint id, string detail) =>
        new($"Motion group contract: identity {id} {detail}. One identity "
            + "must be handed the same channel set, in the same order, on "
            + "every call — the group shares a single clock.");

    private static void Prune<TEntry>(
        Dictionary<uint, TEntry> store, int frame)
        where TEntry : Entry
    {
        Stale.Clear();
        foreach (var (key, value) in store)
            if (frame - value.LastFrame > StaleFrames)
                Stale.Add(key);
        foreach (var key in Stale)
            store.Remove(key);
        Stale.Clear();
    }
}

/// <summary>
/// One channel of an elapsed-clock group: the caller supplies the target,
/// Motion returns the current value in the same slot. A scalar rides in
/// X with the other lanes pinned to zero so target equality — and the
/// group restart it triggers — is one comparison for either shape.
/// </summary>
internal struct MotionChannel
{
    public int Channel;

    /// <summary>Color channels interpolate premultiplied, the way a
    /// browser transitions rgba backgrounds of differing alpha; scalars
    /// interpolate straight.</summary>
    public bool Premultiplied;

    public Vector4 Value;

    public readonly float Scalar => Value.X;

    public static MotionChannel Color(int channel, Vector4 target) =>
        new() { Channel = channel, Premultiplied = true, Value = target };

    public static MotionChannel Number(int channel, float target) =>
        new() { Channel = channel, Value = new Vector4(target, 0f, 0f, 0f) };
}
