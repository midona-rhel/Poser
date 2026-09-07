using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Length-preserving single-chain solve; input and output are root first.</summary>
public static class FabrikSolver
{
    public static Vector3[] Solve(IReadOnlyList<Vector3> source, Vector3 root, Vector3 tip,
        FabrikControlMode mode, int iterations)
    {
        if (source.Count is < 2 or > IkChainConfig.MaxDepth + 1)
            throw new ArgumentOutOfRangeException(nameof(source));
        var reverse = mode == FabrikControlMode.Reverse;
        var positions = source.ToArray();
        if (reverse) Array.Reverse(positions);
        var anchor = reverse ? tip : root;
        var target = reverse ? root : tip;
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
        if (reverse) Array.Reverse(positions);
        return positions;
    }

    private static Vector3 Direction(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;
}
