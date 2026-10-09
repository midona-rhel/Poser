using System.Numerics;
using System.Text.Json;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class IkAnchorAndSpanTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Two_joint_end_rotation_is_the_only_rotation_authority_in_every_target_mode(bool end, bool keep)
    {
        foreach (var mode in Enum.GetValues<IkTargetMode>())
        {
            var config = IkChainConfig.DefaultsFor(true, true) with
            { TargetMode = mode, EnforceEndRotation = end, HoldRotation = keep };
            Assert.Equal(end, config.HoldsEndRotation);
            Assert.Equal(keep, (config with { Solver = IkSolver.Ccd }).HoldsEndRotation);
            Assert.Equal(keep, (config with { Solver = IkSolver.Fabrik }).HoldsEndRotation);
            Assert.Equal(keep, (config with { Solver = IkSolver.Rope }).HoldsEndRotation);
        }
    }

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

    [Theory]
    [InlineData(IkSolver.TwoJoint)]
    [InlineData(IkSolver.Ccd)]
    public void Native_reach_discards_excess_travel_and_old_target_debt(IkSolver solver)
    {
        var config = IkChainConfig.DefaultsFor(true, true) with { Solver = solver };
        var reach = IkReach.FromChain([Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2], config);
        var target = reach.Move(Vector3.UnitX, Vector3.UnitX * 100);
        Near(new(2, 0, 0), target);
        Near(new(1.9f, 0, 0), reach.Move(target, new(-.1f, 0, 0)));
        Near(new(1.9f, 0, 0), reach.Move(new(100, 0, 0), new(-.1f, 0, 0)));
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

    [Theory]
    [InlineData(IkSolver.Rope)]
    [InlineData(IkSolver.Fabrik)]
    public void Repeated_depth_round_trips_reuse_original_rest_geometry_and_anchors(IkSolver solver)
    {
        var original = Control();
        Vector3[] Solve(FabrikControl control)
        {
            var source = control.Bones.Select(b => b.Position).ToArray();
            return solver == IkSolver.Rope
                ? RopeSolver.Solve(source, control.Root.Position, control.Handle.Position, -Vector3.UnitY)
                : FabrikSolver.Solve(source, control.HandleIndex, control.Root.Position,
                    control.Tip.Position, control.Handle.Position, 60);
        }
        var first = Solve(original);
        var current = original;
        for (int i = 0; i < 5; i++)
        {
            current = Select(current, Keys(1, 6), 5);
            _ = Solve(current);
            current = Select(current, Keys(0, 7), 6);
            Assert.Equal(original.Bones, current.Bones);
            Assert.Equal(original.Root, current.Root);
            Assert.Equal(original.Tip, current.Tip);
            Assert.Equal(original.Handle, current.Handle);
            Assert.Equal(original.SwivelBaseline, current.SwivelBaseline);
            var result = Solve(current);
            for (int j = 0; j < first.Length; j++) Near(first[j], result[j]);
        }
        if (solver == IkSolver.Rope)
            Assert.True(Length(first) < Length(original.Bones.Select(b => b.Position).ToArray()) - .01f);
    }

    [Fact]
    public void Both_depth_directions_and_zero_span_select_the_same_reference_without_mutating_history()
    {
        var original = Control();
        var initial = Select(original, Keys(2, 3), 1);
        var grown = Select(initial, Keys(0, 7), 3);
        var zero = Select(grown, Keys(3, 1), 0);
        var restored = Select(zero, Keys(2, 3), 1);
        Assert.Equal(initial.Bones, restored.Bones);
        Assert.Equal(initial.Root, restored.Root);
        Assert.Equal(initial.Tip, restored.Tip);
        Assert.Equal(3, initial.Bones.Length);
        Assert.Equal(7, original.Bones.Length);
        Assert.Single(zero.Bones);
        Assert.Null(zero.Validate());
        var roundTrip = JsonSerializer.Deserialize<FabrikControl>(JsonSerializer.Serialize(grown, Json), Json)!;
        Assert.Equal(grown.ReferenceBones, roundTrip.ReferenceBones);
        Assert.Equal(initial.Bones, Select(roundTrip, Keys(2, 3), 1).Bones);
    }

    [Fact]
    public void Reference_validation_rejects_duplicates_and_nonfinite_geometry()
    {
        var control = Control();
        var reference = control.ReferenceBones!;
        Assert.NotNull((control with { ReferenceBones = [reference[0], reference[0]] }).Validate());
        Assert.NotNull((control with { ReferenceBones = [reference[0] with
            { Pose = reference[0].Pose with { Position = new(float.NaN) } }] }).Validate());
    }

    [Fact]
    public void Zero_translation_preserves_an_out_of_reach_target()
    {
        var reach = new IkReach(Vector3.Zero, 1, 2, Vector3.UnitX);
        Near(new(10, 0, 0), reach.Move(new(10, 0, 0), Vector3.Zero));
        Near(new(.5f, 0, 0), reach.Move(new(.5f, 0, 0), Vector3.Zero));
        Near(new(1.9f, 0, 0), reach.Move(new(10, 0, 0), new(-.1f, 0, 0)));
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

    private static float Length(Vector3[] points) => points.Zip(points.Skip(1), Vector3.Distance).Sum();
    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-5f);
}
