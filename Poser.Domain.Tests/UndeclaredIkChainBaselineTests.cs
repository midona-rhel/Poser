using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

/// <summary>
/// Verifies IK settings for a bone that is not part of a configured arm or leg
/// chain. CCD can discover its joints by walking parents from the selected
/// bone. The two-joint solver requires named joints from a declared chain and
/// must therefore be rejected here.
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
        // The general values are valid; only use without a declared chain fails.
        Assert.Null(twoJoint.Validate());
    }
}
