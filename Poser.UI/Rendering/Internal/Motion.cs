using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// The ONE keyed animation store. Components own no transition
/// dictionaries: they hand Motion the stable ImGui identity of the thing
/// being animated plus a channel number, and read the current value back,
/// so keying, the per-frame advance and eviction all live in one place.
///
/// Two models exist because the components genuinely need two:
/// <list type="bullet">
/// <item><b>Constant-rate</b> (<see cref="Progress"/>) — a symmetric 0..1
/// ramp that runs forward while a condition holds and backward while it
/// does not. A hover that reverses mid-flight retraces exactly the
/// distance it already covered instead of replaying a full duration.</item>
/// <item><b>Elapsed-clock</b> (<see cref="Toward(uint, in Transition,
/// Span{MotionChannel})"/>) — a retargeting clock: every target change
/// captures the current value as the new origin and replays a full
/// duration from it. All channels under one identity share the clock, so
/// a multi-channel state (background + opacity) restarts as one element
/// when any single channel retargets, the way a CSS transition list on
/// one element does.</item>
/// </list>
/// </summary>
internal static class Motion
{
    // Eviction: transient state is only worth sweeping once a store is
    // genuinely large, and only when a miss proves the caller set is
    // churning. Anything not drawn for a couple of frames is gone.
    //
    // The threshold counts ENTRIES, not controls — one entry per
    // (identity, channel). A two-channel control such as the icon button
    // therefore occupies two of these, so the sweep starts at 256 such
    // controls, not 512. This is an eviction heuristic, never a rendering
    // rule: the only observable consequence is that an identity gone for
    // more than StaleFrames snaps to its targets when it returns.
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

    private sealed class TowardEntry : Entry
    {
        public Vector4 Value;
        public Vector4 From;
        public Vector4 Target;
        public float Elapsed;
    }

    private static readonly Dictionary<ulong, RampEntry> Ramps = new();
    private static readonly Dictionary<ulong, TowardEntry> Towards = new();

    // Reused across prunes: the sweep cannot remove while enumerating.
    private static readonly List<ulong> Stale = new();

    private static ulong Key(uint id, int channel) =>
        ((ulong)id << 32) | (uint)channel;

    /// <summary>
    /// Constant-rate linear progress for (<paramref name="id"/>,
    /// <paramref name="channel"/>): 0..1 covered in
    /// <paramref name="durationSeconds"/>, forward while
    /// <paramref name="on"/> and backward otherwise. Returns the RAW
    /// progress — the caller applies its own easing, because the same
    /// ramp can drive several eased properties. First sight seeds at the
    /// resting end of the requested state so a control that appears
    /// already hovered does not fade in.
    /// </summary>
    internal static float Progress(
        uint id, int channel, bool on, float durationSeconds)
    {
        int frame = ImGui.GetFrameCount();
        ulong key = Key(id, channel);
        if (!Ramps.TryGetValue(key, out var entry))
        {
            if (Ramps.Count > PruneThreshold)
                Prune(Ramps, frame);
            entry = new RampEntry { Progress = on ? 1f : 0f };
            Ramps[key] = entry;
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
    /// Elapsed-clock retarget for one scalar channel. Equivalent to a
    /// single-channel group.
    /// </summary>
    internal static float Toward(
        uint id, int channel, float target, in Transition transition)
    {
        Span<MotionChannel> one = [MotionChannel.Number(channel, target)];
        Toward(id, transition, one);
        return one[0].Scalar;
    }

    /// <summary>
    /// Elapsed-clock retarget for one color channel. Equivalent to a
    /// single-channel group.
    /// </summary>
    internal static Vector4 Toward(
        uint id, int channel, Vector4 target, in Transition transition)
    {
        Span<MotionChannel> one = [MotionChannel.Color(channel, target)];
        Toward(id, transition, one);
        return one[0].Value;
    }

    /// <summary>
    /// Elapsed-clock retarget for a whole group of channels under one
    /// identity. The caller writes each channel's TARGET into
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
    /// A newly seen identity — or one drawn twice in the same frame,
    /// which means its clock cannot be trusted — snaps to its targets
    /// with the clock already spent, so controls appear in their resting
    /// state rather than animating in from nothing.
    ///
    /// An identity must be handed the SAME channel set every frame: the
    /// shared clock only stays shared while every channel is advanced
    /// together. A caller that drops, adds or collides on a channel
    /// throws rather than silently animating one half of a control
    /// against a stale clock.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel set for
    /// <paramref name="id"/> differs from the one already stored.
    /// </exception>
    internal static void Toward(
        uint id, in Transition transition, Span<MotionChannel> channels)
    {
        int frame = ImGui.GetFrameCount();
        bool seed = false;
        bool retarget = false;
        // Lockstep audit, O(channels) and allocation-free: the group's
        // entries either all exist sharing one LastFrame, or none do.
        bool present = false;
        int groupFrame = 0;
        int missing = 0;
        bool anyMissing = false;
        for (int i = 0; i < channels.Length; i++)
        {
            if (!Towards.TryGetValue(Key(id, channels[i].Channel), out var probe))
            {
                if (!anyMissing)
                {
                    anyMissing = true;
                    missing = channels[i].Channel;
                }
                seed = true;
                continue;
            }
            if (present && probe.LastFrame != groupFrame)
                throw Desync(
                    id,
                    channels[i].Channel,
                    $"last advanced on frame {probe.LastFrame} while the "
                        + $"rest of the group advanced on {groupFrame}");
            present = true;
            groupFrame = probe.LastFrame;
            if (frame <= probe.LastFrame)
                seed = true;
            else if (probe.Target != channels[i].Value)
                retarget = true;
        }
        if (present && anyMissing)
            throw Desync(
                id,
                missing,
                "is absent while sibling channels of the same identity are "
                    + "stored — the identity was used with another channel set");

        if (seed)
        {
            if (Towards.Count > PruneThreshold)
                Prune(Towards, frame);
            for (int i = 0; i < channels.Length; i++)
            {
                var target = channels[i].Value;
                Towards[Key(id, channels[i].Channel)] = new TowardEntry
                {
                    Value = target,
                    From = target,
                    Target = target,
                    Elapsed = transition.DurationSeconds,
                    LastFrame = frame,
                };
            }
            // The channels already carry their targets, which are also
            // their seeded values.
            return;
        }

        for (int i = 0; i < channels.Length; i++)
        {
            var entry = Towards[Key(id, channels[i].Channel)];
            if (retarget)
            {
                entry.From = entry.Value;
                entry.Target = channels[i].Value;
                entry.Elapsed = 0f;
            }
            else if (entry.Elapsed < transition.DurationSeconds)
            {
                entry.Elapsed = MathF.Min(
                    transition.DurationSeconds,
                    entry.Elapsed + ImGui.GetIO().DeltaTime);
                float linear = transition.DurationSeconds > 0f
                    ? entry.Elapsed / transition.DurationSeconds
                    : 1f;
                float eased = transition.Evaluate(linear);
                entry.Value = channels[i].Premultiplied
                    ? Crystarium.PremultipliedLerp(
                        entry.From, entry.Target, eased)
                    : new Vector4(
                        entry.From.X
                            + (entry.Target.X - entry.From.X) * eased,
                        0f,
                        0f,
                        0f);
            }
            entry.LastFrame = frame;
            channels[i].Value = entry.Value;
        }
    }

    // Built only on the failing path, so the audit above stays free.
    private static InvalidOperationException Desync(
        uint id, int channel, string detail) =>
        new($"Motion group desync: identity {id} channel {channel} {detail}. "
            + "Every frame must pass one identity the same channel set — "
            + "the group shares a single clock.");

    private static void Prune<TEntry>(
        Dictionary<ulong, TEntry> store, int frame)
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
