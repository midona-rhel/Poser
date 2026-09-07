using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Existing hanging-curve solve, shared by either side of a selected handle.</summary>
public static class RopeSolver
{
    public static Vector3[] Solve(IReadOnlyList<Vector3> source, Vector3 root, Vector3 target, Vector3 down)
    {
        var original = source.ToArray();
        var count = source.Count;
        var lengths = new float[count - 1];
        float rope = 0f;
        for (int i = 0; i < count - 1; i++)
        {
            lengths[i] = Vector3.Distance(original[i + 1], original[i]);
            rope += lengths[i];
        }
        if (rope < 1e-5f)
            return Enumerable.Repeat(root, count).ToArray();
        var span = target - root;
        // The hanging plane: the horizontal direction from root to target,
        // or, for a target straight above or below, the direction the chain
        // already leans in, or +X when it leans nowhere.
        var horizontal = span - Vector3.Dot(span, down) * down;
        float d = horizontal.Length();
        Vector3 across;
        if (d > 1e-5f)
            across = horizontal / d;
        else
        {
            var lean = Vector3.Zero;
            for (int i = 1; i < count; i++)
                lean += original[i] - root;
            lean -= Vector3.Dot(lean, down) * down;
            across = lean.LengthSquared() > 1e-10f ? Vector3.Normalize(lean) : Vector3.UnitX;
        }
        float h = -Vector3.Dot(span, down); // target height above the root
        float straight = MathF.Sqrt(d * d + h * h);
        var samples = new Vector3[count];
        samples[0] = root;
        if (straight >= rope - 1e-5f)
        {
            var direction = straight > 1e-6f ? span / straight : down;
            float along = 0f;
            for (int i = 1; i < count; i++)
            {
                along += lengths[i - 1];
                samples[i] = root + direction * along;
            }
        }
        else
        {
            // Catenary y = a cosh(x/a) through both ends with arc length
            // rope: sqrt(rope² − h²) = 2a sinh(d/2a), a found by bisection.
            float chord = MathF.Sqrt(MathF.Max(rope * rope - h * h, 1e-8f));
            float a;
            if (d < 1e-4f)
            {
                // Ends stacked: the rope folds straight down and back up.
                a = 1e-3f;
            }
            else
            {
                float low = 1e-4f, high = 1e4f;
                for (int step = 0; step < 80; step++)
                {
                    float mid = MathF.Sqrt(low * high);
                    float value = 2f * mid * MathF.Sinh(d / (2f * mid));
                    if (value > chord)
                        low = mid;
                    else
                        high = mid;
                }
                a = MathF.Sqrt(low * high);
            }
            // Curve parameters: x from the root along 'across', y up.
            // x0 is where the lowest point sits relative to the root.
            float x0 = d < 1e-4f ? 0f : 0.5f * d - a * MathF.Asinh(h / (2f * a * MathF.Sinh(d / (2f * a))));
            float Y(float x) => a * (MathF.Cosh((x - x0) / a) - MathF.Cosh(-x0 / a));
            float S(float x) => a * (MathF.Sinh((x - x0) / a) - MathF.Sinh(-x0 / a)); // arc length from the root
            if (d < 1e-4f)
            {
                // Down half the slack, then back up to the target.
                float half = 0.5f * (rope - MathF.Abs(h));
                float along = 0f;
                for (int i = 1; i < count; i++)
                {
                    along += lengths[i - 1];
                    float depthDown = h >= 0f ? half : half + MathF.Abs(h);
                    float y = along <= depthDown ? -along : -depthDown + (along - depthDown);
                    samples[i] = root - down * y;
                }
            }
            else
            {
                float along = 0f;
                for (int i = 1; i < count; i++)
                {
                    along += lengths[i - 1];
                    // x for this arc length, by bisection on S.
                    float lowX = 0f, highX = d;
                    for (int step = 0; step < 40; step++)
                    {
                        float midX = 0.5f * (lowX + highX);
                        if (S(midX) < along)
                            lowX = midX;
                        else
                            highX = midX;
                    }
                    float x = 0.5f * (lowX + highX);
                    samples[i] = root + across * x - down * Y(x);
                }
            }
            samples[count - 1] = target;
        }
        return samples;
    }
}
