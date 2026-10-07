using System.Numerics;
using System.Text.Json;
using Poser.Domain.Posing;

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

        // An ancestor (including one outside the solved span) changes the bone's
        // current model transform, not this actor-model target. Actor movement
        // instead applies the actor frame exactly once to the resolved point.
        var actorFrame = Matrix4x4.CreateScale(1.7f) * Matrix4x4.CreateRotationY(.7f)
            * Matrix4x4.CreateTranslation(4, 5, 6);
        Near(Vector3.Transform(anchor.Position, actorFrame), Vector3.Transform(unchanged.Position, actorFrame));
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
            current = current.SelectSpan(Keys(1, 6), 5);
            _ = Solve(current);
            current = current.SelectSpan(Keys(0, 7), 6);
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
        var initial = original.SelectSpan(Keys(2, 3), 1);
        var grown = initial.SelectSpan(Keys(0, 7), 3);
        var zero = grown.SelectSpan(Keys(3, 1), 0);
        var restored = zero.SelectSpan(Keys(2, 3), 1);
        Assert.Equal(initial.Bones, restored.Bones);
        Assert.Equal(initial.Root, restored.Root);
        Assert.Equal(initial.Tip, restored.Tip);
        Assert.Equal(3, initial.Bones.Length);
        Assert.Equal(7, original.Bones.Length);
        Assert.Single(zero.Bones);
        Assert.Null(zero.Validate());
        var roundTrip = JsonSerializer.Deserialize<FabrikControl>(JsonSerializer.Serialize(grown, Json), Json)!;
        Assert.Equal(grown.ReferenceBones, roundTrip.ReferenceBones);
        Assert.Equal(initial.Bones, roundTrip.SelectSpan(Keys(2, 3), 1).Bones);
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
