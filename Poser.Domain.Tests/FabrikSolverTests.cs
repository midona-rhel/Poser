using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class FabrikSolverTests
{
    private static readonly Vector3[] Bent = Enumerable.Range(0, 7).Select(i => new Vector3(i, i % 2, 0)).ToArray();

    [Fact]
    public void Reach_limit_discards_excess_travel_and_reverses_immediately()
    {
        const float initialTarget = 20f;
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
    public void Selected_handle_moves_and_far_ends_stay_anchored(int handle)
    {
        // Capturing the current pose as the target leaves the chain untouched.
        var still = FabrikSolver.Solve(Bent, handle, Bent[0], Bent[^1], Bent[handle], 8);
        for (int i = 0; i < Bent.Length; i++) Near(Bent[i], still[i]);

        var target = Bent[handle] + new Vector3(0, .15f, .3f);
        var result = FabrikSolver.Solve(Bent, handle, Bent[0], Bent[^1], target, 60);
        Near(target, result[handle]);
        if (handle > 0) Near(Bent[0], result[0]);
        if (handle < Bent.Length - 1) Near(Bent[^1], result[^1]);
        Lengths(Bent, result);
    }

    [Fact]
    public void Unreachable_drag_clamps_the_handle_without_stretching()
    {
        var result = FabrikSolver.Solve(Bent, 3, Bent[0], Bent[^1], new(100, 100, 100), 60);
        Near(Bent[0], result[0]); Near(Bent[^1], result[^1]);
        Lengths(Bent, result);

        // A taut chain cannot be stretched by moving its middle.
        Vector3[] taut = [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2];
        var held = FabrikSolver.Solve(taut, 1, taut[0], taut[^1], Vector3.One, 8);
        for (int i = 0; i < taut.Length; i++) Near(taut[i], held[i]);
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
