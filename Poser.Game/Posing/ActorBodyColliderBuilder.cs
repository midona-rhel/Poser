using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Game.Posing;

internal static class ActorBodyColliderBuilder
{
    internal sealed record Joint(Vector3 Position, string? Parent);
    internal sealed record Fitted(string Name, IkCollider Collider);
    private sealed record Span(string Name, string Start, string End, bool FitEnds = false, bool Sphere = false);

    internal static Fitted[] Fit(IReadOnlyDictionary<string, Joint> joints,
        IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, IReadOnlyList<string?> influences)
    {
        var spans = new List<Span> {
            new("Waist", "j_kosi", "j_kubi"), new("Head", "j_kao", "j_kubi", true, true) };
        foreach (var side in new[] { "l", "r" })
        {
            string label = side == "l" ? "Left" : "Right";
            spans.AddRange([new($"{label} upper arm", $"j_ude_a_{side}", $"j_ude_b_{side}"), new($"{label} forearm", $"j_ude_b_{side}", $"j_te_{side}"),
                new($"{label} hand", $"j_te_{side}", $"j_naka_a_{side}", true, true), new($"{label} thigh", $"j_asi_a_{side}", $"j_asi_b_{side}"),
                new($"{label} lower leg", $"j_asi_b_{side}", $"j_asi_d_{side}"), new($"{label} foot", $"j_asi_d_{side}", $"j_asi_e_{side}", true)]);
        }
        spans.RemoveAll(s => !joints.ContainsKey(s.Start) || !joints.ContainsKey(s.End) ||
            Vector3.DistanceSquared(joints[s.Start].Position, joints[s.End].Position) < 1e-10f);
        if (spans.Count == 0) throw new InvalidDataException("This actor has no supported humanoid body chains.");
        var roots = spans.Select((s, i) => (s.Start, i)).ToDictionary(x => x.Start, x => x.i);
        if (roots.TryGetValue("j_kosi", out int waist))
            foreach (var spine in new[] { "n_hara", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi" }) roots[spine] = waist;
        var samples = spans.Select(_ => new List<Vector3>()).ToArray();
        var owners = new Dictionary<string, int>();
        int Owner(string name)
        {
            if (owners.TryGetValue(name, out int cached)) return cached;
            string? current = name;
            int result = -1;
            for (int depth = 0; current != null && depth < joints.Count; depth++)
            {
                // Hair, tails and skirt chains are not body volume. Do not let
                // a long accessory inflate the head or waist.
                if (current.StartsWith("j_kami", StringComparison.Ordinal) || current.StartsWith("j_sippo", StringComparison.Ordinal) ||
                    current.StartsWith("j_sk_", StringComparison.Ordinal)) break;
                if (roots.TryGetValue(current, out result)) break;
                result = -1;
                current = joints.TryGetValue(current, out var joint) ? joint.Parent : null;
            }
            return owners[name] = result;
        }
        foreach (int index in indices.Distinct())
            if (influences[index] is { } bone && Owner(bone) is var part && part >= 0)
                samples[part].Add(vertices[index]);
        var triangles = spans.Select(_ => new List<(Vector3 A, Vector3 B, Vector3 C)>()).ToArray();
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            var ownersOfTriangle = new[] { indices[i], indices[i + 1], indices[i + 2] }
                .Select(v => influences[v] is { } name ? Owner(name) : -1).Where(p => p >= 0).Distinct();
            foreach (int part in ownersOfTriangle)
                triangles[part].Add((vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]));
        }
        var fitted = new List<Fitted>();
        for (int i = 0; i < spans.Count; i++)
        {
            var points = samples[i];
            if (points.Count == 0) continue;
            var span = spans[i];
            var start = joints[span.Start].Position;
            var end = joints[span.End].Position;
            var axis = Vector3.Normalize(end - start);
            var axial = points.Select(p => Vector3.Dot(p - start, axis)).Order().ToArray();
            if (span.FitEnds)
            {
                end = start + axis * axial[(int)((axial.Length - 1) * .98f)];
                start += axis * axial[(int)((axial.Length - 1) * .02f)];
            }
            var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < .9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(axis, u);
            var center = (start + end) * .5f;
            if (span.FitEnds)
            {
                // Center head/hands/feet on the surface, not the attachment
                // joint. Feet keep the ankle-to-toe direction of the posed rig.
                Vector3 Middle(Vector3 direction)
                {
                    var values = points.Select(p => Vector3.Dot(p - center, direction)).Order().ToArray();
                    return direction * (values[(int)((values.Length - 1) * .05f)] + values[(int)((values.Length - 1) * .95f)]) * .5f;
                }
                center += Middle(u) + Middle(v);
            }
            var widths = new List<float>();
            foreach (float t in span.Sphere ? new[] { .5f } : new[] { .25f, .5f, .75f })
            {
                var origin = center + axis * ((t - .5f) * Vector3.Distance(start, end));
                var directions = span.Sphere ? new[] { u, -u, v, -v, axis, -axis } : new[] { u, -u, v, -v };
                var distances = directions.Select(d => NearestSurface(origin, d, triangles[i])).ToArray();
                if (distances.All(float.IsFinite)) widths.Add(distances.Average());
            }
            // Average the sampled surface distances rather than choosing the
            // narrowest side. Open surfaces use mean vertex distance instead.
            float radius = widths.Count > 0 ? widths.Average()
                : points.Select(p => span.Sphere ? Vector3.Distance(p, center)
                    : (p - center - axis * Vector3.Dot(p - center, axis)).Length()).Average();
            float length = Vector3.Distance(start, end);
            if (!span.Sphere) radius = MathF.Min(radius, length * .5f);
            if (radius < .0001f) continue;
            var cross = Vector3.Cross(Vector3.UnitY, axis);
            var rotation = span.Sphere ? Quaternion.Identity : axis.Y < -.999999f
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) : Quaternion.Normalize(new Quaternion(cross, 1 + axis.Y));
            fitted.Add(new(span.Name, new IkCollider { Shape = span.Sphere ? IkColliderShape.Sphere : IkColliderShape.Capsule,
                Transform = new(center, rotation, new(radius * 2, span.Sphere ? radius * 2 : length, radius * 2)) }));
        }
        if (fitted.Count == 0) throw new InvalidDataException("No visible body surface could be fitted to the actor's bones.");
        return fitted.ToArray();
    }

    private static float NearestSurface(Vector3 origin, Vector3 direction, List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        float nearest = float.PositiveInfinity;
        foreach (var (a, b, c) in triangles)
        {
            var e1 = b - a; var e2 = c - a;
            var cross = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, cross);
            if (MathF.Abs(det) < 1e-10f) continue;
            var offset = origin - a;
            float u = Vector3.Dot(offset, cross) / det;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(offset, e1);
            float v = Vector3.Dot(direction, q) / det;
            if (v < 0 || u + v > 1) continue;
            float t = Vector3.Dot(e2, q) / det;
            if (t > .00001f) nearest = MathF.Min(nearest, t);
        }
        return nearest;
    }
}
