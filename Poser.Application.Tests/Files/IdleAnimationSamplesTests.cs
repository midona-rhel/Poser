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

    [Fact]
    public void Rejects_non_finite_sampling()
    {
        var pose = Pose("body", Target(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdleAnimationSamples.Create(pose, pose, float.NaN, 30));
    }

    [Fact]
    public void Long_native_entry_keeps_its_full_duration()
    {
        var pose = Pose("body", Target(1));
        var samples = IdleAnimationSamples.Create(pose, pose, 215f / 30f);
        Assert.Equal(215f / 30f, samples.Body.Entry.DurationSeconds);
        Assert.Equal(216, samples.Body.Entry.Frames.Length);
        Assert.Equal(samples.Body.Hold.Frames[0], samples.Body.Entry.Frames[^1]);
    }
}
