using System.Numerics;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Game.Posing;

/// <summary>Bounded, detached surface fitting. No game reads or runtime mesh collision.</summary>
internal static class BodySurfaceFit
{
    internal readonly record struct Sample(Vector3 Position, float Area);
    private readonly record struct Capsule(Vector3 Center, Vector3 Axis, float Radius, float HalfStem);

    internal static IkCollider Fit(IReadOnlyList<Sample> surface, Vector3 origin,
        Quaternion frame, Vector3 direction, bool roundOnly, bool freeAxis)
    {
        if (frame.LengthSquared() < .5f) frame = Quaternion.Identity;
        frame = Quaternion.Normalize(frame);
        var inverse = Quaternion.Conjugate(frame);
        // Area-stratified samples bound optimization independently of mesh density.
        // Vertices retain triangle-area weights, so subdividing a face does not
        // give it more authority than the rest of the body.
        var points = Resample(surface, 768).Select(p => Vector3.Transform(p - origin, inverse)).ToArray();
        if (points.Length < 4) throw new InvalidDataException("A body section has too little surface to fit.");
        var low = new Vector3(Quantile(points.Select(p => p.X), .01f),
            Quantile(points.Select(p => p.Y), .01f), Quantile(points.Select(p => p.Z), .01f));
        var high = new Vector3(Quantile(points.Select(p => p.X), .99f),
            Quantile(points.Select(p => p.Y), .99f), Quantile(points.Select(p => p.Z), .99f));
        var size = high - low;
        float extent = size.Length();
        if (!float.IsFinite(extent) || extent < .0001f)
            throw new InvalidDataException("A body section has no finite surface extent.");
        // Remove isolated spikes in three dimensions, not the innermost surface.
        // Slack retains rounded ends and low-density extremities.
        var slack = size * .1f + new Vector3(extent * .005f);
        points = points.Where(p => p.X >= low.X - slack.X && p.X <= high.X + slack.X &&
            p.Y >= low.Y - slack.Y && p.Y <= high.Y + slack.Y &&
            p.Z >= low.Z - slack.Z && p.Z <= high.Z + slack.Z).ToArray();
        if (points.Length < 4) throw new InvalidDataException("A body section has insufficient supported surface.");

        var center = (low + high) * .5f;
        var primary = Vector3.Transform(direction, inverse);
        primary = primary.LengthSquared() > 1e-12f ? Vector3.Normalize(primary) : Vector3.UnitY;
        var axes = freeAxis && !roundOnly
            ? new[] { primary, PrincipalAxis(points), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }
            : new[] { primary };
        Capsule best = default;
        float bestCost = float.PositiveInfinity;
        foreach (var axis in axes)
        {
            float a = Quantile(points.Select(p => Vector3.Dot(p - center, axis)), .01f);
            float b = Quantile(points.Select(p => Vector3.Dot(p - center, axis)), .99f);
            var axisCenter = center + axis * ((a + b) * .5f);
            float radius = roundOnly
                ? Quantile(points.Select(p => Vector3.Distance(p, axisCenter)), .75f)
                : Quantile(points.Select(p => (p - axisCenter - axis * Vector3.Dot(p - axisCenter, axis)).Length()), .7f);
            radius = MathF.Max(radius, extent * .01f);
            var candidate = new Capsule(axisCenter, axis, radius, roundOnly ? 0 : MathF.Max(0, (b - a) * .5f - radius));
            var (refined, cost) = Refine(points, candidate, extent, roundOnly, false, 12);
            candidate = refined;
            if (cost < bestCost) { bestCost = cost; best = candidate; }
        }
        // Covariance and bone axes are only seeds, not the final orientation.
        // A bounded final search corrects unevenly weighted/open surfaces without
        // tilting limb drivers or splitting sections into additional shapes.
        if (freeAxis && !roundOnly)
            (best, _) = Refine(points, best, extent, false, true, 24);
        bool sphere = roundOnly || best.HalfStem < best.Radius * .08f;
        var worldAxis = Vector3.Transform(best.Axis, frame);
        var cross = Vector3.Cross(Vector3.UnitY, worldAxis);
        var rotation = worldAxis.Y < -.999999f
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)
            : Quaternion.Normalize(new Quaternion(cross, 1 + worldAxis.Y));
        var result = new IkCollider
        {
            Shape = sphere ? IkColliderShape.Sphere : IkColliderShape.Capsule,
            Transform = new(origin + Vector3.Transform(best.Center, frame), rotation,
                IkCollider.CapsuleScale(best.Radius, sphere ? 0 : best.HalfStem * 2)),
        };
        if (!result.Transform.IsValid) throw new InvalidDataException("A body section produced an invalid collider.");
        return result;
    }

    private static (Capsule Fit, float Cost) Refine(Vector3[] points, Capsule candidate,
        float extent, bool roundOnly, bool rotate, int iterations)
    {
        float cost = Cost(points, candidate, extent);
        float step = extent * (rotate ? .04f : .08f), angle = .12f;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            for (int component = 0; component < (rotate ? 8 : roundOnly ? 4 : 5); component++)
            {
                var seed = candidate;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var trial = seed;
                    if (component < 3)
                    {
                        var center = seed.Center;
                        center[component] += step * sign;
                        trial = trial with { Center = center };
                    }
                    else if (component == 3)
                        trial = trial with { Radius = Math.Clamp(seed.Radius + step * sign, extent * .005f, extent) };
                    else if (component == 4)
                        trial = trial with { HalfStem = Math.Clamp(seed.HalfStem + step * sign, 0, extent) };
                    else
                    {
                        var around = component == 5 ? Vector3.UnitX : component == 6 ? Vector3.UnitY : Vector3.UnitZ;
                        trial = trial with { Axis = Vector3.Normalize(Vector3.Transform(seed.Axis,
                            Quaternion.CreateFromAxisAngle(around, angle * sign))) };
                    }
                    float next = Cost(points, trial, extent);
                    if (next < cost) { cost = next; candidate = trial; }
                }
            }
            if ((iteration + 1) % (rotate ? 4 : 2) == 0) { step *= .5f; angle *= .5f; }
        }
        return (candidate, cost);
    }

    private static float Cost(Vector3[] points, Capsule capsule, float extent)
    {
        float sum = 0, limit = extent * .08f;
        foreach (var p in points)
        {
            var d = p - capsule.Center;
            float axial = Math.Clamp(Vector3.Dot(d, capsule.Axis), -capsule.HalfStem, capsule.HalfStem);
            float error = (d - capsule.Axis * axial).Length() - capsule.Radius;
            float magnitude = MathF.Abs(error);
            // Outside samples represent clipping; inside samples represent air
            // between the mesh and collider. Penalize both, with modest coverage
            // preference, and prevent a single accessory from dominating.
            float loss = magnitude <= limit ? error * error : limit * (2 * magnitude - limit);
            sum += loss * (error > 0 ? 2 : 1);
        }
        return sum / points.Length;
    }

    internal static float Distance(IkCollider collider, Vector3 p)
    {
        var local = Vector3.Transform(p - collider.Transform.Position, Quaternion.Conjugate(collider.Transform.Rotation));
        var (radius, stem) = collider.RoundDimensions();
        local.Y -= Math.Clamp(local.Y, -stem * .5f, stem * .5f);
        return local.Length() - radius;
    }

    private static Vector3[] Resample(IReadOnlyList<Sample> points, int count)
    {
        double total = points.Sum(p => (double)p.Area);
        if (!(total > 0) || !double.IsFinite(total)) throw new InvalidDataException("Invalid body surface area.");
        var result = new Vector3[count];
        double cumulative = points[0].Area;
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            double target = total * (i + .5) / count;
            while (cumulative < target && index + 1 < points.Count) cumulative += points[++index].Area;
            result[i] = points[index].Position;
        }
        return result;
    }

    private static float Quantile(IEnumerable<float> values, float fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)((sorted.Length - 1) * fraction)];
    }

    private static Vector3 PrincipalAxis(Vector3[] points)
    {
        var mean = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Length;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in points)
        {
            var d = p - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        var axis = xx >= yy && xx >= zz ? Vector3.UnitX : yy >= zz ? Vector3.UnitY : Vector3.UnitZ;
        for (int i = 0; i < 20; i++)
        {
            var next = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z,
                xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            if (next.LengthSquared() < 1e-20f) break;
            axis = Vector3.Normalize(next);
        }
        return axis;
    }
}
