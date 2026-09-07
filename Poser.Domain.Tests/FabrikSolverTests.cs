using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class FabrikSolverTests
{
    private static readonly Vector3[] Bent = [Vector3.Zero, new(1, 1, 0), new(2, 0, 0), new(3, 1, 0)];

    [Theory]
    [InlineData(FabrikControlMode.Forward)]
    [InlineData(FabrikControlMode.Reverse)]
    [InlineData(FabrikControlMode.Bidirectional)]
    public void Captured_endpoints_do_not_change_the_pose(FabrikControlMode mode)
    {
        var result = FabrikSolver.Solve(Bent, Bent[0], Bent[^1], mode, 8);
        for (int i = 0; i < Bent.Length; i++) Near(Bent[i], result[i]);
    }

    [Theory]
    [InlineData(FabrikControlMode.Forward)]
    [InlineData(FabrikControlMode.Reverse)]
    [InlineData(FabrikControlMode.Bidirectional)]
    public void Reachable_targets_preserve_both_ends_and_lengths(FabrikControlMode mode)
    {
        var root = new Vector3(2, 3, 1);
        var tip = new Vector3(4, 4, 2);
        var result = FabrikSolver.Solve(Bent, root, tip, mode, 60);
        Near(root, result[0]); Near(tip, result[^1]); Lengths(Bent, result);
    }

    [Theory]
    [InlineData(FabrikControlMode.Forward)]
    [InlineData(FabrikControlMode.Reverse)]
    [InlineData(FabrikControlMode.Bidirectional)]
    public void Unreachable_targets_keep_the_priority_end_without_stretching(FabrikControlMode mode)
    {
        var root = new Vector3(-100, 0, 0); var tip = new Vector3(100, 0, 0);
        var result = FabrikSolver.Solve(Bent, root, tip, mode, 8);
        if (mode == FabrikControlMode.Reverse) Near(tip, result[^1]);
        else Near(root, result[0]);
        Lengths(Bent, result);
    }

    [Fact]
    public void Fifty_links_are_supported_and_fifty_one_rejected()
    {
        var chain = Enumerable.Range(0, 51).Select(i => new Vector3(i, i % 2, 0)).ToArray();
        Lengths(chain, FabrikSolver.Solve(chain, chain[0], new(40, 2, 1), FabrikControlMode.Forward, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => FabrikSolver.Solve(
            new Vector3[52], Vector3.Zero, Vector3.One, FabrikControlMode.Forward, 8));
        Assert.Equal(50, IkChainConfig.MaxDepthFor(IkSolver.Fabrik));
        Assert.Equal(20, IkChainConfig.MaxDepthFor(IkSolver.Ccd));
    }

    [Fact]
    public void A_straight_chain_can_bend_toward_a_closer_collinear_target()
    {
        Vector3[] chain = [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2, Vector3.UnitX * 3];
        var tip = Vector3.UnitX * 1.5f;
        var result = FabrikSolver.Solve(chain, Vector3.Zero, tip, FabrikControlMode.Forward, 60);
        Near(tip, result[^1]); Lengths(chain, result);
    }

    [Fact]
    public void Zero_length_links_and_coincident_targets_stay_finite()
    {
        Vector3[] chain = [Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector3.Zero];
        var result = FabrikSolver.Solve(chain, Vector3.Zero, Vector3.Zero, FabrikControlMode.Reverse, 60);
        Lengths(chain, result);
        Assert.All(result, p => Assert.True(float.IsFinite(p.LengthSquared())));
    }

    [Fact]
    public void Solve_is_equivariant_under_actor_rotation_and_translation()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(.5f, -.2f, 1);
        Vector3 Move(Vector3 v) => Vector3.Transform(v, rotation) + new Vector3(7, -3, 9);
        var a = FabrikSolver.Solve(Bent, Vector3.Zero, new(2, 1, 1), FabrikControlMode.Bidirectional, 60);
        var b = FabrikSolver.Solve(Bent.Select(Move).ToArray(), Move(Vector3.Zero), Move(new(2, 1, 1)),
            FabrikControlMode.Bidirectional, 60);
        for (int i = 0; i < a.Length; i++) Near(Move(a[i]), b[i]);
    }

    private static void Lengths(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> result)
    {
        for (int i = 1; i < source.Count; i++)
            Assert.InRange(MathF.Abs(Vector3.Distance(source[i - 1], source[i])
                - Vector3.Distance(result[i - 1], result[i])), 0, .001f);
    }
    private static void Near(Vector3 a, Vector3 b) => Assert.InRange(Vector3.Distance(a, b), 0, .001f);
}
