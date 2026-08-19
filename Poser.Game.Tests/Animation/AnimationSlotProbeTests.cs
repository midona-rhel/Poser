using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Animation;

namespace Poser.Game.Tests.Animation;

public sealed class AnimationSlotProbeTests
{
    private static readonly ActorId ActorA = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
    private static readonly ActorId ActorB = new(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 2);

    [Fact]
    public void Probe_records_boundaries_without_invoking_a_runtime_write()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var before = Snapshot("base=3", "0.0", "a");
        var after = Snapshot("base=42", "0.0", "b");

        Assert.True(probe.Start(ActorA, "actor-a", before).Success);
        var command = new AnimationProbeCommand("selection", AnimationSlot.Base, 42);
        probe.Begin(ActorA, "actor-a", command, before);
        probe.Complete(ActorA, "actor-a", command, true, after);

        Assert.Contains(lines, line => line.Contains(" BEGIN "));
        Assert.Contains(lines, line => line.Contains(" CMD "));
        Assert.Contains(lines, line => line.Contains(" RESULT "));
        Assert.DoesNotContain(lines, line => line.Contains("write", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Probe_rejects_another_actor_without_attributing_it()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var snapshot = Snapshot("base=3", "0.0", "a");
        Assert.True(probe.Start(ActorA, "actor-a", snapshot).Success);

        probe.Begin(
            ActorB,
            "actor-b",
            new AnimationProbeCommand("selection", AnimationSlot.UpperBody, 42),
            Snapshot("base=42", "0.1", "b"));

        Assert.Single(lines);
        Assert.DoesNotContain("bbbbbbbb", string.Join('\n', lines));
        Assert.True(probe.IsActiveFor(ActorA));
    }

    [Fact]
    public void Probe_ends_on_actor_replacement_and_gpose_exit()
    {
        var replacementLines = new List<string>();
        var replacement = new AnimationSlotProbe(replacementLines.Add);
        Assert.True(replacement.Start(ActorA, "actor-a", Snapshot("base=3", "0.0", "a")).Success);
        replacement.Tick("actor-replaced", Snapshot("base=3", "0.0", "a"), true);
        Assert.Contains(replacementLines, line => line.Contains("END") && line.Contains("actor-replaced"));

        var exitLines = new List<string>();
        var exit = new AnimationSlotProbe(exitLines.Add);
        Assert.True(exit.Start(ActorA, "actor-a", Snapshot("base=3", "0.0", "a")).Success);
        exit.Tick("actor-a", Snapshot("base=3", "0.0", "a"), false);
        Assert.Contains(exitLines, line => line.Contains("END") && line.Contains("gpose-exit"));
    }

    [Fact]
    public void Probe_limits_scopes_and_records_an_end_marker()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var snapshot = Snapshot("base=3", "0.0", "a");
        Assert.True(probe.Start(ActorA, "actor-a", snapshot).Success);

        for (int index = 0; index < AnimationSlotProbe.MaximumScopes; index++)
        {
            var command = new AnimationProbeCommand("slot-speed", AnimationSlot.Base);
            probe.Begin(ActorA, "actor-a", command, snapshot);
            probe.Complete(ActorA, "actor-a", command, true, snapshot);
        }
        probe.Begin(
            ActorA,
            "actor-a",
            new AnimationProbeCommand("slot-speed", AnimationSlot.Base),
            snapshot);

        Assert.True(lines.Count <= AnimationSlotProbe.MaximumRecords);
        Assert.Contains(lines, line => line.Contains("END") && line.Contains("scope-capacity"));
    }

    [Fact]
    public void Probe_requires_two_exclusive_same_slot_changes_for_a_candidate()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var before = Snapshot("base=3", "0.0", "a");
        var after = Snapshot("base=42", "0.1", "b");
        Assert.True(probe.Start(ActorA, "actor-a", before).Success);
        var first = new AnimationProbeCommand("selection", AnimationSlot.Base, 42);
        var second = new AnimationProbeCommand("selection", AnimationSlot.Base, 43);

        probe.Begin(ActorA, "actor-a", first, before);
        probe.Complete(ActorA, "actor-a", first, true, after);
        probe.Begin(ActorA, "actor-a", second, before);
        probe.Complete(ActorA, "actor-a", second, true, after);
        Assert.DoesNotContain(lines, line => line.Contains("CANDIDATE"));
        Assert.True(probe.Stop(ActorA, "actor-a", after).Success);

        Assert.Contains(lines, line => line.Contains("CANDIDATE") && line.Contains("slot=Base"));
    }

    [Fact]
    public void Probe_rejects_a_candidate_shared_by_two_slots()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var before = Snapshot("base=3", "0.0", "a");
        var after = Snapshot("base=42", "0.1", "b");
        Assert.True(probe.Start(ActorA, "actor-a", before).Success);

        foreach (var slot in new[] { AnimationSlot.Base, AnimationSlot.UpperBody })
        {
            foreach (var timeline in new ushort[] { 42, 43 })
            {
                var command = new AnimationProbeCommand("selection", slot, timeline);
                probe.Begin(ActorA, "actor-a", command, before);
                probe.Complete(ActorA, "actor-a", command, true, after);
            }
        }
        Assert.True(probe.Stop(ActorA, "actor-a", after).Success);

        Assert.DoesNotContain(lines, line => line.Contains("CANDIDATE"));
    }

    [Fact]
    public void Probe_rejects_repeated_single_timeline_evidence()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var before = Snapshot("base=3", "0.0", "a");
        var after = Snapshot("base=42", "0.1", "b");
        Assert.True(probe.Start(ActorA, "actor-a", before).Success);
        var command = new AnimationProbeCommand("selection", AnimationSlot.Base, 42);

        probe.Begin(ActorA, "actor-a", command, before);
        probe.Complete(ActorA, "actor-a", command, true, after);
        probe.Begin(ActorA, "actor-a", command, before);
        probe.Complete(ActorA, "actor-a", command, true, after);
        Assert.True(probe.Stop(ActorA, "actor-a", after).Success);

        Assert.DoesNotContain(lines, line => line.Contains("CANDIDATE"));
    }

    [Fact]
    public void Probe_dispose_ends_once_and_ignores_later_ticks()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var snapshot = Snapshot("base=3", "0.0", "a");
        Assert.True(probe.Start(ActorA, "actor-a", snapshot).Success);

        probe.Dispose();
        int count = lines.Count;
        probe.Tick("actor-a", snapshot, true);

        Assert.Contains(lines, line => line.Contains("END") && line.Contains("dispose"));
        Assert.Equal(count, lines.Count);
    }

    [Fact]
    public void Probe_keeps_the_full_diagnostic_story_with_post_command_samples()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var before = Snapshot("base=3 upper=0", "0.0", "a");
        var after = Snapshot("base=42 upper=43 speed=0.08", "0.1", "a");
        Assert.True(probe.Start(ActorA, "actor-a", before).Success);

        foreach (var command in new[]
        {
            new AnimationProbeCommand("selection", AnimationSlot.Base, 3),
            new AnimationProbeCommand("selection", AnimationSlot.Base, 42),
            new AnimationProbeCommand("selection", AnimationSlot.UpperBody, 43),
            new AnimationProbeCommand("slot-speed", AnimationSlot.Base),
            new AnimationProbeCommand("slot-loop", AnimationSlot.Base, 42),
            new AnimationProbeCommand("slot-loop", AnimationSlot.UpperBody, 43),
        })
        {
            probe.Begin(ActorA, "actor-a", command, before);
            probe.Complete(ActorA, "actor-a", command, true, after);
            for (int frame = 0; frame < 5; frame++)
                probe.Tick("actor-a", after, true);
        }

        Assert.True(probe.IsActiveFor(ActorA));
        Assert.Equal(6, lines.Count(line => line.Contains(" CMD ")));
        Assert.True(lines.Count(line => line.Contains(" SAMPLE ")) >= 1);
        Assert.True(probe.Stop(ActorA, "actor-a", after).Success);
    }

    [Fact]
    public void Probe_logs_loop_arm_and_disarm_intent()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var snapshot = Snapshot("base=42", "0.0", "a");
        Assert.True(probe.Start(ActorA, "actor-a", snapshot).Success);

        foreach (bool enabled in new[] { true, false })
        {
            var command = new AnimationProbeCommand(
                "slot-loop", AnimationSlot.Base, 42, enabled);
            probe.Begin(ActorA, "actor-a", command, snapshot);
            probe.Complete(ActorA, "actor-a", command, true, snapshot);
        }

        Assert.Contains(lines, line => line.Contains("command=slot-loop") && line.Contains("intent=on"));
        Assert.Contains(lines, line => line.Contains("command=slot-loop") && line.Contains("intent=off"));
    }

    [Fact]
    public void Probe_ends_at_the_conservative_timeout()
    {
        var lines = new List<string>();
        var probe = new AnimationSlotProbe(lines.Add);
        var snapshot = Snapshot("base=3", "0.0", "a");
        Assert.True(probe.Start(ActorA, "actor-a", snapshot).Success);

        for (int frame = 0; frame < AnimationSlotProbe.MaximumFrames; frame++)
            probe.Tick("actor-a", snapshot, true);

        Assert.Contains(lines, line => line.Contains("END") && line.Contains("timeout"));
        Assert.False(probe.IsActiveFor(ActorA));
    }

    private static SlotProbeSnapshot Snapshot(string state, string controlState, string fingerprint) =>
        new(state, [new SlotProbeControl("0.0", fingerprint, controlState)]);
}
