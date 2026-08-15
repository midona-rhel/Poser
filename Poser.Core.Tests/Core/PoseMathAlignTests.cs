using System;
using System.Numerics;
using Poser.Core;

namespace Poser.Tests.Core;

/// <summary>
/// AlignZTo carries the light-spawn facing: local +Z (the beam axis the
/// overlay draws and game lights throw along) must land exactly on the
/// requested world direction, as a proper rotation, for any direction the
/// camera can look — including straight down.
/// </summary>
public class PoseMathAlignTests
{
    private const float Tolerance = 1e-5f;

    private static void AssertAligned(Vector3 direction)
    {
        var rotation = PoseMath.AlignZTo(direction);
        var mapped = Vector3.Transform(Vector3.UnitZ, rotation);
        var expected = Vector3.Normalize(direction);
        Assert.True(Vector3.Distance(expected, mapped) < Tolerance,
            $"+Z should map to {expected}, got {mapped}");
        Assert.True(MathF.Abs(rotation.Length() - 1f) < Tolerance,
            "rotation must stay unit length");
    }

    [Fact]
    public void CardinalDirections_MapBeamExactly()
    {
        AssertAligned(Vector3.UnitX);
        AssertAligned(-Vector3.UnitX);
        AssertAligned(Vector3.UnitZ);
        AssertAligned(-Vector3.UnitZ);
    }

    [Fact]
    public void NearVertical_UsesTheFallbackReferenceAxis()
    {
        AssertAligned(Vector3.UnitY);
        AssertAligned(-Vector3.UnitY);
        AssertAligned(new Vector3(0.01f, 1f, 0.01f));
    }

    [Fact]
    public void ArbitraryDiagonals_StayProperRotations()
    {
        AssertAligned(new Vector3(1f, -0.5f, 2f));
        AssertAligned(new Vector3(-3f, 0.2f, -1f));
        // Handedness: X cross Y must equal Z, not -Z (a mirror would still
        // map the beam correctly while flipping the light's local frame).
        var rotation = PoseMath.AlignZTo(new Vector3(1f, -0.5f, 2f));
        var x = Vector3.Transform(Vector3.UnitX, rotation);
        var y = Vector3.Transform(Vector3.UnitY, rotation);
        var z = Vector3.Transform(Vector3.UnitZ, rotation);
        Assert.True(Vector3.Distance(Vector3.Cross(x, y), z) < Tolerance,
            "frame must stay right-handed");
    }

    [Fact]
    public void DegenerateDirection_ReturnsIdentity()
    {
        Assert.Equal(Quaternion.Identity, PoseMath.AlignZTo(Vector3.Zero));
        Assert.Equal(
            Quaternion.Identity,
            PoseMath.AlignZTo(new Vector3(float.NaN, 0f, 0f)));
    }
}
