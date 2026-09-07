using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class FabrikSolverTests
{
    private static readonly Vector3[] Bent = Enumerable.Range(0, 7).Select(i => new Vector3(i, i % 2, 0)).ToArray();

    [Theory]
    [InlineData(3f)]
    [InlineData(20f)]
    public void Reach_limit_discards_excess_travel_and_reverses_immediately(float initialTarget)
    {
        Vector3[] source = [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2, Vector3.UnitX * 3];
        var target = new Vector3(initialTarget, 0, 0);
        target = FabrikSolver.MoveHandle(source, 3, source[0], source[^1], target, Vector3.UnitX * 10);
        Near(new(3, 0, 0), target);
        target = FabrikSolver.MoveHandle(source, 3, source[0], source[^1], target, new(-.1f, 0, 0));
        Near(new(2.9f, 0, 0), target);
        Near(target, FabrikSolver.Solve(source, 3, source[0], source[^1], target, 60)[3]);
        // Correct an old, already-overextended target on the first inward move too.
        Near(new(2.9f, 0, 0), FabrikSolver.MoveHandle(source, 3, source[0], source[^1],
            new(initialTarget, 0, 0), new(-.1f, 0, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void Capturing_either_or_both_sides_preserves_the_pose(int handle)
    {
        var result = FabrikSolver.Solve(Bent, handle, Bent[0], Bent[^1], Bent[handle], 8);
        for (int i = 0; i < Bent.Length; i++) Near(Bent[i], result[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void Selected_handle_moves_and_far_ends_stay_anchored(int handle)
    {
        var target = Bent[handle] + new Vector3(0, .15f, .3f);
        var result = FabrikSolver.Solve(Bent, handle, Bent[0], Bent[^1], target, 60);
        Near(target, result[handle]);
        if (handle > 0) Near(Bent[0], result[0]);
        if (handle < Bent.Length - 1) Near(Bent[^1], result[^1]);
        Lengths(Bent, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void Unreachable_drag_clamps_the_handle_without_stretching(int handle)
    {
        var result = FabrikSolver.Solve(Bent, handle, Bent[0], Bent[^1], new(100, 100, 100), 60);
        if (handle > 0) Near(Bent[0], result[0]);
        if (handle < Bent.Length - 1) Near(Bent[^1], result[^1]);
        Lengths(Bent, result);
    }

    [Fact]
    public void Two_single_link_sides_share_one_handle_on_their_intersection_circle()
    {
        Vector3[] chain = [Vector3.Zero, new(1, 1, 0), new(2, 0, 0)];
        var result = FabrikSolver.Solve(chain, 1, chain[0], chain[^1], new(1, 0, 4), 8);
        Near(new(1, 0, 1), result[1]);
        Near(chain[0], result[0]); Near(chain[^1], result[^1]); Lengths(chain, result);
    }

    [Fact]
    public void Taut_two_sided_chain_cannot_be_stretched_by_moving_its_middle()
    {
        Vector3[] chain = [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2];
        var result = FabrikSolver.Solve(chain, 1, chain[0], chain[^1], Vector3.One, 8);
        for (int i = 0; i < chain.Length; i++) Near(chain[i], result[i]);
    }

    [Fact]
    public void Default_iterations_keep_the_two_spans_connected()
    {
        var target = Bent[3] + new Vector3(0, .15f, .3f);
        var result = FabrikSolver.Solve(Bent, 3, Bent[0], Bent[^1], target, 8);
        Near(target, result[3]); Near(Bent[0], result[0]); Near(Bent[^1], result[^1]);
        Lengths(Bent, result);
    }

    [Fact]
    public void Fifty_links_are_supported_and_fifty_one_rejected()
    {
        var chain = Enumerable.Range(0, 51).Select(i => new Vector3(i, i % 2, 0)).ToArray();
        Lengths(chain, FabrikSolver.Solve(chain, 25, chain[0], chain[^1], new(25, 3, 1), 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => FabrikSolver.Solve(
            new Vector3[52], 25, Vector3.Zero, Vector3.One, Vector3.UnitX, 8));
        Assert.Equal(50, IkChainConfig.MaxDepthFor(IkSolver.Fabrik));
        Assert.Equal(20, IkChainConfig.MaxDepthFor(IkSolver.Ccd));
    }

    [Fact]
    public void Depths_share_the_fifty_link_limit_and_preserve_parent_traversal_by_default()
    {
        var config = IkChainConfig.DefaultsForChain();
        Assert.Equal(3, config.ParentDepth); Assert.Equal(0, config.ChildDepth);
        Assert.Null((config with { ParentDepth = 25, ChildDepth = 25 }).Validate());
        Assert.NotNull((config with { ParentDepth = 26, ChildDepth = 25 }).Validate());
        Assert.NotNull((config with { ParentDepth = -1 }).Validate());
        Assert.Null((config with { ParentDepth = 0, ChildDepth = 0 }).Validate());
    }

    [Fact]
    public void Straight_chain_can_bend_toward_a_closer_collinear_handle()
    {
        Vector3[] chain = [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2, Vector3.UnitX * 3];
        var result = FabrikSolver.Solve(chain, 3, chain[0], chain[^1], Vector3.UnitX * 1.5f, 60);
        Near(Vector3.UnitX * 1.5f, result[^1]); Lengths(chain, result);
    }

    [Fact]
    public void Zero_length_links_and_coincident_targets_stay_finite()
    {
        Vector3[] chain = [Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector3.Zero];
        var result = FabrikSolver.Solve(chain, 1, chain[0], chain[^1], Vector3.Zero, 60);
        Lengths(chain, result);
        Assert.All(result, p => Assert.True(float.IsFinite(p.LengthSquared())));
    }

    [Fact]
    public void Solve_follows_actor_rotation_and_translation()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(.5f, -.2f, 1);
        Vector3 Move(Vector3 v) => Vector3.Transform(v, rotation) + new Vector3(7, -3, 9);
        var target = new Vector3(3, 1, 1);
        var a = FabrikSolver.Solve(Bent, 3, Bent[0], Bent[^1], target, 60);
        var b = FabrikSolver.Solve(Bent.Select(Move).ToArray(), 3, Move(Bent[0]), Move(Bent[^1]), Move(target), 60);
        for (int i = 0; i < a.Length; i++) Near(Move(a[i]), b[i]);
    }

    [Fact]
    public void Rope_keeps_its_hanging_curve_on_either_side()
    {
        Vector3[] chain = [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0), new(3, 1, 0), new(4, 0, 0)];
        var result = RopeSolver.Solve(chain, chain[0], chain[^1], -Vector3.UnitY);
        Near(chain[0], result[0]); Near(chain[^1], result[^1]);
        Assert.True(result[2].Y < 0);
        var reversed = RopeSolver.Solve(chain.Reverse().ToArray(), chain[^1], chain[0], -Vector3.UnitY);
        for (int i = 0; i < result.Length; i++) Near(result[i], reversed[result.Length - 1 - i]);
    }

    private static void Lengths(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> result)
    {
        for (int i = 1; i < source.Count; i++)
            Assert.InRange(MathF.Abs(Vector3.Distance(source[i - 1], source[i])
                - Vector3.Distance(result[i - 1], result[i])), 0, .001f);
    }
    private static void Near(Vector3 a, Vector3 b) => Assert.InRange(Vector3.Distance(a, b), 0, .001f);
}
