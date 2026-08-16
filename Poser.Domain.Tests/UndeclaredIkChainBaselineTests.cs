using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

/// <summary>
/// Acceptance for a chain that has no declared definition — CCD armed on an
/// arbitrary bone.
///
/// <para><see cref="IkPolicy"/> stays the FIXED-chain decision: it answers for
/// the four declared arm and leg endpoints only. A bone that heads no chain is
/// judged by <see cref="IkChainConfig.ValidateUndeclared"/> instead, whose one
/// extra rule is that Two Joint needs a definition's named joints and twists
/// while CCD needs nothing but the endpoint's own parent walk. That is Brio's
/// split: every bone carries CCD options, and Two Joint is offered
/// additionally for <c>j_te*</c> / <c>j_asi_d*</c>.</para>
/// </summary>
public sealed class UndeclaredIkChainBaselineTests
{
    [Fact]
    public void Ccd_defaults_are_Brios_depth_three_and_eight_iterations()
    {
        var defaults = IkChainConfig.DefaultsForCcd();

        Assert.Equal(IkSolver.Ccd, defaults.Solver);
        Assert.Equal(3, defaults.CcdDepth);
        Assert.Equal(8, defaults.CcdIterations);
        Assert.False(defaults.Enabled);
        Assert.Equal(IkTargetMode.Relative, defaults.TargetMode);
        Assert.True(defaults.EnforceConstraints);
        Assert.Null(defaults.ValidateUndeclared());
    }

    [Fact]
    public void Ccd_defaults_can_be_created_already_armed()
    {
        Assert.True(IkChainConfig.DefaultsForCcd(enabled: true).Enabled);
    }

    [Fact]
    public void Two_joint_is_refused_on_a_bone_with_no_declared_chain()
    {
        var twoJoint = IkChainConfig.DefaultsForCcd() with
        {
            Solver = IkSolver.TwoJoint,
        };

        Assert.NotNull(twoJoint.ValidateUndeclared());
        // The configuration itself is perfectly valid — it is only the
        // UNDECLARED reading of it that refuses.
        Assert.Null(twoJoint.Validate());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 8)]
    [InlineData(20, 60)]
    public void The_whole_ccd_parameter_range_survives_the_undeclared_gate(
        int depth,
        int iterations)
    {
        var config = IkChainConfig.DefaultsForCcd() with
        {
            CcdDepth = depth,
            CcdIterations = iterations,
        };

        Assert.Null(config.ValidateUndeclared());
        Assert.Equal(depth, config.Normalized().CcdDepth);
        Assert.Equal(iterations, config.Normalized().CcdIterations);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(21, 8)]
    [InlineData(3, 0)]
    [InlineData(3, 61)]
    public void Out_of_range_depth_or_iterations_never_reach_the_solver(
        int depth,
        int iterations)
    {
        var config = IkChainConfig.DefaultsForCcd() with
        {
            CcdDepth = depth,
            CcdIterations = iterations,
        };

        Assert.NotNull(config.ValidateUndeclared());
    }

    [Fact]
    public void Undeclared_defaults_keep_a_usable_hinge_so_a_later_switch_validates()
    {
        // The Two Joint fields are dead weight for CCD, but a zero hinge axis
        // would make the configuration unvalidatable the moment the bone did
        // turn out to head a declared chain.
        var defaults = IkChainConfig.DefaultsForCcd();

        Assert.NotEqual(Vector3.Zero, defaults.HingeAxis);
        Assert.Null((defaults with { Solver = IkSolver.TwoJoint }).Validate());
    }
}
