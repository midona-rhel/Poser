using System.Collections.Immutable;
using System.Numerics;
using Poser.Documents.Animation;
using Poser.Domain.Transforms;

namespace Poser.Tests.Files;

public sealed class IdleAnimationSamplesTests
{
    private static IdleSkeletonPose Pose(string name, PoseTransform target) =>
        new(name, 2, [new(1, PoseTransform.Identity, target)]);
    private static PoseTransform Target(float x) => new(new(x, 0, 0),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), new(2));

    [Fact]
    public void Body_and_expression_hold_independently_without_replaying_transitions()
    {
        var samples = IdleAnimationSamples.Create(Pose("body", Target(2)), Pose("face", Target(0.1f)));
        foreach (var binding in new[] { samples.Body, samples.Expression })
        {
            Assert.Equal(PoseTransform.Identity, binding.Entry.Frames[0][0]);
            Assert.Equal(2, binding.Hold.Frames.Length);
            Assert.Equal(binding.Entry.Frames[^1][0], binding.Hold.Frames[0][0]);
            Assert.Equal(binding.Hold.Frames[0][0], binding.Hold.Frames[1][0]);
            Assert.Equal(binding.Hold.Frames[0][0], binding.Exit.Frames[0][0]);
            Assert.Equal(PoseTransform.Identity, binding.Exit.Frames[^1][0]);
            Assert.True(binding.Hold.DurationSeconds > 0);
        }
        Assert.Equal(2, samples.Body.Hold.Frames[0][0].Position.X);
        Assert.Equal(0.1f, samples.Expression.Hold.Frames[0][0].Position.X);
        Assert.Equal(samples.Body.Entry.Frames.Length, samples.Expression.Entry.Frames.Length);
    }

    [Fact]
    public void Transitions_use_sine_easing_and_shortest_path_rotation()
    {
        var target = Target(2);
        var samples = IdleAnimationSamples.Create(Pose("body", target), Pose("face", target), 1, 4);
        var frames = samples.Body.Entry.Frames;
        Assert.Equal(5, frames.Length);
        Assert.InRange(frames[1][0].Position.X, 0.29289f, 0.29290f);
        Assert.InRange(frames[2][0].Position.X, 0.99999f, 1.00001f);
        var halfway = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4);
        Assert.True(MathF.Abs(Quaternion.Dot(halfway, frames[2][0].Rotation)) > 0.99999f);
        Assert.Equal(frames[1][0], samples.Body.Exit.Frames[3][0]);
        var antipodal = PoseTransform.Identity with { Rotation = -Quaternion.Identity };
        var still = IdleAnimationSamples.Create(Pose("body", antipodal), Pose("face", antipodal), 1, 4);
        Assert.All(still.Body.Entry.Frames, frame =>
            Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Identity, frame[0].Rotation)) > 0.99999f));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Rejects_out_of_skeleton_indices(short index)
    {
        var bad = new IdleSkeletonPose("body", 2, [new(index, PoseTransform.Identity, Target(1))]);
        Assert.Throws<ArgumentException>(() => IdleAnimationSamples.Create(bad, Pose("face", Target(1))));
    }

    [Fact]
    public void Rejects_missing_expression_and_duplicate_bindings()
    {
        var body = Pose("body", Target(1));
        Assert.Throws<ArgumentException>(() => IdleAnimationSamples.Create(body,
            new("face", 2, ImmutableArray<IdleBoneTrack>.Empty)));
        Assert.Throws<ArgumentException>(() => IdleAnimationSamples.Create(body,
            new("face", 2, [body.Tracks[0], body.Tracks[0]])));
    }

    [Fact]
    public void Rejects_corrupt_transforms_and_singular_scale_transitions()
    {
        var body = Pose("body", Target(1));
        foreach (var invalid in new[] {
            Target(1) with { Position = new(float.NaN) },
            Target(1) with { Rotation = default },
            Target(1) with { Scale = Vector3.Zero },
            Target(1) with { Scale = new(-1, 1, 1) } })
            Assert.ThrowsAny<ArgumentException>(() => IdleAnimationSamples.Create(body, Pose("face", invalid)));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(float.NaN, 30)]
    [InlineData(float.PositiveInfinity, 30)]
    [InlineData(6, 30)]
    [InlineData(0.3f, 0)]
    [InlineData(0.3f, 121)]
    public void Rejects_invalid_or_unbounded_sampling(float duration, int rate)
    {
        var pose = Pose("body", Target(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdleAnimationSamples.Create(pose, pose, duration, rate));
    }
}
