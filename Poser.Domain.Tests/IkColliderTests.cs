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

}
