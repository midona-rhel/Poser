using System.Numerics;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Xunit;

namespace Poser.Domain.Tests;

public class IkColliderTests
{
    [Theory]
    [InlineData(IkColliderShape.Box)]
    [InlineData(IkColliderShape.Cylinder)]
    [InlineData(IkColliderShape.Cone)]
    public void SegmentInteriorHitsEvenWhenBothEndsAreOutside(IkColliderShape shape)
    {
        var geometry = new ColliderGeometry(new() { Shape = shape });
        Assert.True(geometry.Contact(new(-2, 0, 0), new(2, 0, 0), .02f, out var t, out var correction));
        Assert.InRange(t, 0.01f, .99f);
        Assert.True(TransformMath.IsFinite(correction));
        Assert.True(correction.Length() > .1f);
    }

    [Fact]
    public void PlaneIsFiniteAndTwoSided()
    {
        var geometry = new ColliderGeometry(new() { Shape = IkColliderShape.Plane });
        Assert.True(geometry.Contact(new(0, -1, 0), new(0, 1, 0), .05f, out _, out _));
        Assert.True(geometry.Contact(new(0, 1, 0), new(0, -1, 0), .05f, out _, out _));
        Assert.False(geometry.Contact(new(2, -1, 0), new(2, 1, 0), .05f, out _, out _));
    }

    [Fact]
    public void RotatedScaledColliderUsesWorldSpaceWidth()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var geometry = new ColliderGeometry(new() { Transform = new(new(10, 0, 0), rotation, new(2, 1, 3)) });
        Assert.True(geometry.Contact(new(10, -3, 0), new(10, 3, 0), .1f, out _, out _));
        Assert.False(geometry.Contact(new(11, -3, 0), new(11, 3, 0), .1f, out _, out _));
    }

    [Fact]
    public void CollisionPassPreservesPinsAndFiniteLengths()
    {
        var positions = new[] { new Vector3(-1, 0, 0), new Vector3(-.4f, -.5f, 0), new Vector3(.4f, -.5f, 0), new Vector3(1, 0, 0) };
        var before = positions.ToArray();
        var geometry = new ColliderGeometry(new() { Transform = new(new(0, -.6f, 0), Quaternion.Identity, new(.5f)) });
        IkCollisionSolver.Solve(positions, 3, new[] { geometry }, .04f, 32);
        Assert.Equal(before[0], positions[0]);
        Assert.Equal(before[^1], positions[^1]);
        for (int i = 0; i < positions.Length - 1; i++)
        {
            Assert.True(TransformMath.IsFinite(positions[i]));
            Assert.InRange(MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - Vector3.Distance(before[i], before[i + 1])), 0, .002f);
            if (geometry.Contact(positions[i], positions[i + 1], .04f, out _, out var correction))
                Assert.InRange(correction.Length(), 0f, .002f);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void SlackChainRoutesAroundBoxAcrossItsMiddle(int iterations)
    {
        var positions = Enumerable.Range(0, 13).Select(i =>
            new Vector3(-1.5f + i * .25f, .8f - 1.2f * MathF.Sin(i * MathF.PI / 12), 0)).ToArray();
        var before = positions.ToArray();
        var geometry = new ColliderGeometry(new());
        IkCollisionSolver.Solve(positions, 12, new[] { geometry }, .04f, iterations);
        Assert.Equal(before[0], positions[0]);
        Assert.Equal(before[^1], positions[^1]);
        for (int i = 0; i < positions.Length - 1; i++)
        {
            Assert.InRange(MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - Vector3.Distance(before[i], before[i + 1])), 0, .002f);
            if (geometry.Contact(positions[i], positions[i + 1], .04f, out _, out var correction))
                Assert.True(correction.Length() <= .002f, $"Link {i} penetrates by {correction.Length()}");
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(.08f, .15f)]
    [InlineData(-.08f, -.1f)]
    public void RopeSettlesFlatOnBoxInsteadOfMakingSmallArches(float boxHeight, float targetOffset)
    {
        var source = Enumerable.Range(0, 21).Select(i => new Vector3(i * .165f, 0, 0)).ToArray();
        var positions = RopeSolver.Solve(source, new(-1.5f, 1, 0), new(1.5f + targetOffset, 1, 0), -Vector3.UnitY);
        var before = positions.ToArray();
        var geometry = new ColliderGeometry(new() { Transform = new(new(0, boxHeight, 0), Quaternion.Identity, new(2, 1, 1)) });
        IkCollisionSolver.Solve(positions, 20, new[] { geometry }, .04f, 32, -Vector3.UnitY);
        Assert.Equal(before[0], positions[0]);
        Assert.Equal(before[^1], positions[^1]);
        for (int i = 0; i < positions.Length - 1; i++)
        {
            Assert.InRange(MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - Vector3.Distance(before[i], before[i + 1])), 0, .002f);
            if (geometry.Contact(positions[i], positions[i + 1], .04f, out _, out var correction))
                Assert.True(correction.Length() <= .002f, $"Link {i} penetrates by {correction.Length()}");
        }
        var supported = positions.Where(p => MathF.Abs(p.X) < .4f && MathF.Abs(p.Z) < .4f).ToArray();
        Assert.NotEmpty(supported);
        Assert.All(supported, p => Assert.InRange(p.Y, boxHeight + .538f, boxHeight + .555f));
    }

    [Fact]
    public void ImpossiblePinnedLinkStaysFiniteAndDoesNotMoveItsAnchors()
    {
        var positions = new[] { new Vector3(-1, 0, 0), new Vector3(1, 0, 0) };
        var before = positions.ToArray();
        IkCollisionSolver.Solve(positions, 1, new[] { new ColliderGeometry(new()) }, .1f, 60);
        Assert.Equal(before, positions);
    }
}
