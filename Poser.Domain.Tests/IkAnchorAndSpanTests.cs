using System.Numerics;
using System.Text.Json;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class IkAnchorAndSpanTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Fact]
    public void Actor_anchor_uses_captured_model_point_and_only_subsequent_handle_edits()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f);
        var authored = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .2f);
        var anchor = new IkActorAnchor(new(1, 2, 3), rotation, new(.2f, .3f, .4f), authored);
        var unchanged = anchor.Resolve(anchor.AuthoredPosition, authored);
        Near(anchor.Position, unchanged.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, unchanged.Rotation)) > .99999f);
        Near(anchor.Position + Vector3.UnitZ,
            anchor.Resolve(anchor.AuthoredPosition + Vector3.UnitZ, authored).Position);

        // Actor world motion applies exactly once outside the model-space resolve.
        var actorFrame = Matrix4x4.CreateScale(1.7f) * Matrix4x4.CreateRotationY(.7f)
            * Matrix4x4.CreateTranslation(4, 5, 6);
        Near(Vector3.Transform(anchor.Position, actorFrame), Vector3.Transform(unchanged.Position, actorFrame));
    }

    [Fact]
    public void External_parent_carries_target_and_existing_edits_but_new_drags_use_current_axes()
    {
        var frame = new PoseTransform(new(2, 3, 4), Quaternion.Identity, new(1.2f, .8f, 1.4f));
        var anchor = new IkActorAnchor(new(3, 3, 4), Quaternion.Identity, Vector3.Zero, Quaternion.Identity)
        { ParentName = "j_sako_l", ParentTransform = frame };
        var moved = frame with { Position = new(5, 3, 4), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };
        var before = anchor.Resolve(new(.5f, 0, 0), Quaternion.Identity, frame);
        var after = anchor.Resolve(new(.5f, 0, 0), Quaternion.Identity, moved);
        Near(new(3.5f, 3, 4), before.Position);
        Near(new(5, 4.5f, 4), after.Position);
        // Inside-chain changes do not change this external parent frame.
        Near(after.Position, anchor.Resolve(new(.5f, 0, 0), Quaternion.Identity, moved).Position);
        var step = new Vector3(.1f, -.05f, .03f);
        var stored = new Vector3(.5f, 0, 0) + anchor.ToReferenceDelta(step, moved);
        Near(after.Position + step, anchor.Resolve(stored, Quaternion.Identity, moved).Position);
    }

    [Fact]
    public void Native_reach_discards_excess_travel_and_old_target_debt()
    {
        var config = IkChainConfig.DefaultsFor(true, true) with { Solver = IkSolver.TwoJoint };
        var reach = IkReach.FromChain([Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2], config);
        var target = reach.Move(Vector3.UnitX, Vector3.UnitX * 100);
        Near(new(2, 0, 0), target);
        Near(new(1.9f, 0, 0), reach.Move(target, new(-.1f, 0, 0)));
        Near(new(1.9f, 0, 0), reach.Move(new(100, 0, 0), new(-.1f, 0, 0)));
        // A zero translation leaves an out-of-reach target where it is.
        Near(new(10, 0, 0), new IkReach(Vector3.Zero, 1, 2, Vector3.UnitX).Move(new(10, 0, 0), Vector3.Zero));
    }

    [Fact]
    public void Two_joint_reach_respects_hinge_limits_and_scaled_segment_lengths()
    {
        var config = IkChainConfig.DefaultsFor(true, true) with { HingeMinDegrees = 60, HingeMaxDegrees = 90 };
        var reach = IkReach.FromChain([Vector3.Zero, new(2, 0, 0), new(4, 0, 0)], config);
        Assert.True(MathF.Abs(reach.Min - 2) < 1e-5f);
        Assert.True(MathF.Abs(reach.Max - MathF.Sqrt(8)) < 1e-5f);
        Near(new(reach.Min, 0, 0), reach.Clamp(Vector3.Zero));
        Near(new(reach.Max, 0, 0), reach.Clamp(new(20, 0, 0)));
    }

    [Fact]
    public void Actor_capture_survives_configuration_history_and_serialization()
    {
        var config = IkChainConfig.DefaultsFor(true, true) with
        {
            ActorAnchor = new(new(1, 2, 3), Quaternion.Identity, new(.2f, 0, 0), Quaternion.Identity),
        };
        var restored = JsonSerializer.Deserialize<IkChainConfig>(JsonSerializer.Serialize(config, Json), Json)!;
        Assert.Equal(config, restored);
        Assert.Null(restored.Validate());
        Assert.NotNull((config with { ActorAnchor = config.ActorAnchor!.Value with { Position = new(float.NaN) } }).Validate());
    }

    [Fact]
    public void Span_missing_a_newly_available_bone_rejects_without_mutating_the_reference()
    {
        var original = Control();
        Assert.False(original.TrySelectSpan([("b5", 0), ("new_bone", 0)], 0, out var rejected));
        Assert.Same(original, rejected);
        Assert.False(original.TrySelectSpan([("b5", 1)], 0, out rejected));
        Assert.Same(original, rejected);
        Assert.True(original.TrySelectSpan(Keys(0, 7), 6, out var restored));
        Assert.Equal(original.Bones, restored.Bones);
        Assert.Same(original.ReferenceBones, restored.ReferenceBones);

        // Growing, shrinking to zero and returning select the same reference.
        var initial = Select(original, Keys(2, 3), 1);
        var zero = Select(Select(initial, Keys(0, 7), 3), Keys(3, 1), 0);
        Assert.Single(zero.Bones);
        Assert.Equal(initial.Bones, Select(zero, Keys(2, 3), 1).Bones);
    }

    private static FabrikControl Select(FabrikControl control, (string Name, int Partial)[] members, int handle)
    {
        Assert.True(control.TrySelectSpan(members, handle, out var selected));
        return selected;
    }

    private static FabrikControl Control()
    {
        var bones = Enumerable.Range(0, 7).Select(i => new FabrikBonePose($"b{i}", 0,
            new(i, 0, 0), Quaternion.Identity, Vector3.Zero, Quaternion.Identity)).ToArray();
        FabrikTarget Point(FabrikBonePose b) => new(IkTargetMode.Actor, b.Position, b.Rotation,
            b.AuthoredPosition, b.AuthoredRotation, HoldRotation: false);
        return new(bones, Point(bones[0]), Point(bones[^1]), 17, 6,
            Point(bones[^1]) with { Position = new(3, 0, 0) })
        {
            ReferenceBones = bones.Select(b => new FabrikReferenceBone(b, Point(b))).ToArray(),
        };
    }

    private static (string Name, int Partial)[] Keys(int start, int count) =>
        Enumerable.Range(start, count).Select(i => ($"b{i}", 0)).ToArray();

    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-5f);
}
