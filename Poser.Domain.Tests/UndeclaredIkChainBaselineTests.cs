using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

/// <summary>
/// Acceptance for a chain that has no declared definition — CCD armed on an
/// arbitrary bone.
///
/// <para>A bone that heads no declared arm or leg chain is judged by
/// <see cref="IkChainConfig.ValidateUndeclared"/>, whose one extra rule is
/// that Two Joint needs a definition's named joints and twists while CCD
/// needs nothing but the endpoint's own parent walk. That is Brio's
/// split: every bone carries CCD options, and Two Joint is offered
/// additionally for <c>j_te*</c> / <c>j_asi_d*</c>.</para>
/// </summary>
public sealed class UndeclaredIkChainBaselineTests
{
    [Fact]
    public void Chain_defaults_are_Brios_depth_three_and_eight_iterations()
    {
        var defaults = IkChainConfig.DefaultsForChain();

        Assert.Equal(IkSolver.Fabrik, defaults.Solver);
        Assert.Equal(3, defaults.CcdDepth);
        Assert.Equal(8, defaults.CcdIterations);
        Assert.False(defaults.Enabled);
        Assert.Equal(IkTargetMode.Actor, defaults.TargetMode);
        Assert.True(defaults.EnforceConstraints);
        Assert.Null(defaults.ValidateUndeclared());
    }

    [Fact]
    public void Two_joint_is_refused_on_a_bone_with_no_declared_chain()
    {
        var twoJoint = IkChainConfig.DefaultsForChain() with
        {
            Solver = IkSolver.TwoJoint,
        };

        Assert.NotNull(twoJoint.ValidateUndeclared());
        // The configuration itself is perfectly valid — it is only the
        // UNDECLARED reading of it that refuses.
        Assert.Null(twoJoint.Validate());
    }
}
