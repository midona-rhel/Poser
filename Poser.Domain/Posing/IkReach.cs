using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Reach shell for native solvers; rejected travel is never stored as a target offset.</summary>
public readonly record struct IkReach(Vector3 Root, float Min, float Max, Vector3 Fallback)
{
    public Vector3 Clamp(Vector3 target)
    {
        var offset = target - Root;
        var length = offset.Length();
        var direction = length > 1e-7f ? offset / length
            : Fallback.LengthSquared() > 1e-12f ? Vector3.Normalize(Fallback) : Vector3.UnitX;
        return Root + direction * Math.Clamp(length, Min, Max);
    }

    public Vector3 Move(Vector3 target, Vector3 step) => step == Vector3.Zero ? target : Clamp(Clamp(target) + step);

    public static IkReach FromChain(IReadOnlyList<Vector3> points, IkChainConfig config)
    {
        float sum = 0, longest = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var length = Vector3.Distance(points[i - 1], points[i]);
            sum += length;
            longest = MathF.Max(longest, length);
        }
        float min = MathF.Max(0, longest * 2 - sum), max = sum;
        if (config.Solver == IkSolver.TwoJoint && points.Count == 3)
        {
            var a = Vector3.Distance(points[0], points[1]);
            var b = Vector3.Distance(points[1], points[2]);
            float Radius(float angle) => MathF.Sqrt(MathF.Max(0,
                a * a + b * b - 2 * a * b * MathF.Cos(angle * MathF.PI / 180)));
            min = Radius(config.HingeMinDegrees);
            max = Radius(config.HingeMaxDegrees);
        }
        return new(points[0], min, MathF.Max(min, max), points[^1] - points[0]);
    }
}
