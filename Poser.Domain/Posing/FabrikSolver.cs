using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Length-preserving single-chain solve; input and output are root first.</summary>
public static class FabrikSolver
{
    public static Vector3[] Solve(IReadOnlyList<Vector3> source, int handleIndex,
        Vector3 root, Vector3 tip, Vector3 target, int iterations)
    {
        if (source.Count is < 2 or > IkChainConfig.MaxDepth + 1
            || handleIndex < 0 || handleIndex >= source.Count)
            throw new ArgumentOutOfRangeException(nameof(source));
        var parent = source.Take(handleIndex + 1).ToArray();
        var child = source.Skip(handleIndex).Reverse().ToArray();
        target = ClampHandle(source, handleIndex, root, tip, target);
        var positions = source.ToArray();
        if (parent.Length > 1)
            SolveSpan(parent, root, target, iterations).CopyTo(positions, 0);
        if (child.Length > 1)
        {
            var solved = SolveSpan(child, tip, target, iterations);
            Array.Reverse(solved);
            solved.CopyTo(positions, handleIndex);
        }
        if (parent.Length > 1 && child.Length > 1) positions[handleIndex] = target;
        return positions;
    }

    public static Vector3 ClampHandle(IReadOnlyList<Vector3> source, int handleIndex,
        Vector3 root, Vector3 tip, Vector3 target)
    {
        (float Min, float Max) Reach(int first, int last)
        {
            float reach = 0, longest = 0;
            for (int i = first + 1; i <= last; i++)
            {
                var length = Vector3.Distance(source[i - 1], source[i]);
                reach += length;
                longest = MathF.Max(longest, length);
            }
            return (MathF.Max(0, 2 * longest - reach), reach);
        }
        var parent = Reach(0, handleIndex);
        var child = Reach(handleIndex, source.Count - 1);
        Vector3 Project(Vector3 anchor, float radius) => anchor
            + Direction(target - anchor, Direction(source[handleIndex] - anchor, Vector3.UnitX)) * radius;
        if (handleIndex == 0) return Project(tip, Math.Clamp(Vector3.Distance(tip, target), child.Min, child.Max));
        if (handleIndex == source.Count - 1) return Project(root, Math.Clamp(Vector3.Distance(root, target), parent.Min, parent.Max));

        bool Feasible(Vector3 p)
        {
            float a = Vector3.Distance(p, root), b = Vector3.Distance(p, tip);
            return a >= parent.Min - 1e-5f && a <= parent.Max + 1e-5f
                && b >= child.Min - 1e-5f && b <= child.Max + 1e-5f;
        }
        if (Feasible(target)) return target;
        var best = source[handleIndex];
        var distance = float.PositiveInfinity;
        void Consider(Vector3 p)
        {
            var next = Vector3.DistanceSquared(p, target);
            if (Feasible(p) && next < distance) { best = p; distance = next; }
        }
        // The closest point in two reach shells lies on one sphere or on
        // their intersection circle. This also handles taut/single-link spans;
        // alternating projections converge poorly when the spheres just touch.
        Consider(best);
        var delta = tip - root;
        var separation = delta.Length();
        var axis = Direction(delta, Vector3.UnitX);
        foreach (var a in new[] { parent.Min, parent.Max })
        {
            Consider(Project(root, a));
            foreach (var b in new[] { child.Min, child.Max })
            {
                Consider(Project(tip, b));
                if (separation < 1e-6f) continue;
                var along = (a * a - b * b + separation * separation) / (2 * separation);
                var squaredRadius = a * a - along * along;
                if (squaredRadius < -1e-5f) continue;
                var center = root + axis * along;
                var radial = target - center;
                radial -= axis * Vector3.Dot(radial, axis);
                var fallback = source[handleIndex] - center;
                fallback -= axis * Vector3.Dot(fallback, axis);
                if (fallback.LengthSquared() < 1e-12f)
                    fallback = Vector3.Cross(axis, MathF.Abs(axis.X) < .9f ? Vector3.UnitX : Vector3.UnitY);
                Consider(center + Direction(radial, Vector3.Normalize(fallback)) * MathF.Sqrt(MathF.Max(0, squaredRadius)));
            }
        }
        return best;
    }

    private static Vector3[] SolveSpan(IReadOnlyList<Vector3> source, Vector3 anchor, Vector3 target,
        int iterations)
    {
        if (source.Count is < 2 or > IkChainConfig.MaxDepth + 1)
            throw new ArgumentOutOfRangeException(nameof(source));
        var positions = source.ToArray();
        var lengths = new float[positions.Length - 1];
        var directions = new Vector3[lengths.Length];
        float reach = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            var delta = positions[i + 1] - positions[i];
            lengths[i] = delta.Length();
            directions[i] = Direction(delta, Vector3.UnitX);
            reach += lengths[i];
        }
        var offset = anchor - positions[0];
        for (int i = 0; i < positions.Length; i++) positions[i] += offset;
        if (Vector3.Distance(anchor, target) >= reach)
        {
            var direction = Direction(target - anchor, directions[0]);
            for (int i = 0; i < lengths.Length; i++)
                positions[i + 1] = positions[i] + direction * lengths[i];
        }
        else
        {
            // Exactly straight chains have no bend plane; seed one small,
            // deterministic bend only when the requested endpoint has changed.
            var axis = positions[^1] - anchor;
            if (positions.Length > 2 && axis.LengthSquared() > 1e-10f
                && Vector3.DistanceSquared(positions[^1], target) > 1e-8f
                && positions.All(p => Vector3.Cross(p - anchor, axis).LengthSquared() < 1e-10f))
            {
                var unit = Vector3.Normalize(axis);
                var bend = Vector3.Normalize(Vector3.Cross(unit,
                    MathF.Abs(unit.X) < .9f ? Vector3.UnitX : Vector3.UnitY));
                for (int i = 1; i < positions.Length - 1; i++)
                    positions[i] += bend * (reach * .05f * MathF.Sin(MathF.PI * i / (positions.Length - 1)));
            }
            for (int pass = 0; pass < Math.Clamp(iterations, 1, IkChainConfig.MaxIterations); pass++)
            {
                if (Vector3.DistanceSquared(positions[^1], target) < 1e-8f) break;
                positions[^1] = target;
                for (int i = lengths.Length - 1; i >= 0; i--)
                    positions[i] = positions[i + 1]
                        + Direction(positions[i] - positions[i + 1], -directions[i]) * lengths[i];
                positions[0] = anchor;
                for (int i = 0; i < lengths.Length; i++)
                    positions[i + 1] = positions[i]
                        + Direction(positions[i + 1] - positions[i], directions[i]) * lengths[i];
            }
        }
        return positions;
    }

    private static Vector3 Direction(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;
}
