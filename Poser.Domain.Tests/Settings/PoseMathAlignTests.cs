using System;
using System.Numerics;
using Poser.Core;

namespace Poser.Tests.Core;

public sealed class PoseMathAlignTests
{
    private static void AssertAligned(Vector3 direction)
    {
        var rotation = PoseMath.AlignZTo(direction);
        var mapped = Vector3.Transform(Vector3.UnitZ, rotation);
        Assert.True(Vector3.Distance(Vector3.Normalize(direction), mapped) < 1e-5f);
        Assert.InRange(rotation.Length(), 0.99999f, 1.00001f);
    }

    [Fact]
    public void AlignZTo_handles_cardinal_vertical_and_diagonal_directions_as_unit_rotations()
    {
        foreach (var direction in new[]
        {
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
            new Vector3(1f, -0.5f, 2f), new Vector3(-3f, 0.2f, -1f),
        })
            AssertAligned(direction);
    }

    [Fact]
    public void AlignZTo_returns_identity_for_degenerate_input_and_preserves_handedness()
    {
        Assert.Equal(Quaternion.Identity, PoseMath.AlignZTo(Vector3.Zero));
        Assert.Equal(Quaternion.Identity,
            PoseMath.AlignZTo(new Vector3(float.NaN, 0f, 0f)));
        var rotation = PoseMath.AlignZTo(new Vector3(1f, -0.5f, 2f));
        var x = Vector3.Transform(Vector3.UnitX, rotation);
        var y = Vector3.Transform(Vector3.UnitY, rotation);
        var z = Vector3.Transform(Vector3.UnitZ, rotation);
        Assert.True(Vector3.Distance(Vector3.Cross(x, y), z) < 1e-5f);
    }
}
