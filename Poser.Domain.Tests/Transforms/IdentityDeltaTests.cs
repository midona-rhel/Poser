using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests.Transforms;

public sealed class IdentityDeltaTests
{
    [Fact]
    public void Component_and_length_rules_keep_their_own_thresholds()
    {
        var zero = Transform.Zero;
        Assert.True(TransformMath.IsApproximatelyIdentityDelta(zero));
        Assert.True(TransformMath.IsIdentityDeltaByLength(zero));

        // 9e-7 per axis passes the per-component bound but its squared
        // length (2.43e-12) fails the length rule.
        var drift = zero;
        drift.Position = new Vector3(9e-7f);
        Assert.True(TransformMath.IsApproximatelyIdentityDelta(drift));
        Assert.False(TransformMath.IsIdentityDeltaByLength(drift));

        // An unnormalized rotation with |W| > 1 passes only the length rule.
        var stretched = zero;
        stretched.Rotation = new Quaternion(0f, 0f, 0f, 1.001f);
        Assert.False(TransformMath.IsApproximatelyIdentityDelta(stretched));
        Assert.True(TransformMath.IsIdentityDeltaByLength(stretched));
    }
}
