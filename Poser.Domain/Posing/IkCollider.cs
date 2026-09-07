using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

public enum IkColliderShape { Plane, Box, Cylinder, Cone }

public sealed record IkCollider
{
    public IkColliderShape Shape { get; init; } = IkColliderShape.Box;
    public PoseTransform Transform { get; init; } = PoseTransform.Identity;
    public bool Enabled { get; init; } = true;
    public bool Locked { get; init; }

    public IkCollider Normalized() => this with
    {
        Shape = Enum.IsDefined(Shape) ? Shape : IkColliderShape.Box,
        Transform = Transform.IsValid ? Transform : PoseTransform.Identity,
    };
}

/// <summary>Convex world-space geometry shared by rendering and contact queries.</summary>
public sealed class ColliderGeometry
{
    public Vector3[] Vertices { get; }
    public int[][] Faces { get; }
    public (int A, int B)[] Edges { get; }
    private readonly Plane[] _planes;

    public ColliderGeometry(IkCollider collider)
    {
        const int sides = 32;
        var vertices = new List<Vector3>();
        var faces = new List<int[]>();
        var edges = new List<(int, int)>();
        if (collider.Shape is IkColliderShape.Plane or IkColliderShape.Box)
        {
            float height = collider.Shape == IkColliderShape.Plane ? 0f : .5f;
            vertices.AddRange(new[] {
                new Vector3(-.5f,-height,-.5f), new Vector3(.5f,-height,-.5f),
                new Vector3(.5f,-height,.5f), new Vector3(-.5f,-height,.5f),
                new Vector3(-.5f,height,-.5f), new Vector3(.5f,height,-.5f),
                new Vector3(.5f,height,.5f), new Vector3(-.5f,height,.5f) });
            faces.AddRange(new[] { new[]{0,3,2,1}, new[]{4,5,6,7},
                new[]{0,1,5,4}, new[]{1,2,6,5}, new[]{2,3,7,6}, new[]{3,0,4,7} });
            for (int i = 0; i < 4; i++)
            {
                edges.Add((i, (i + 1) % 4));
                if (height > 0) { edges.Add((i + 4, (i + 1) % 4 + 4)); edges.Add((i, i + 4)); }
            }
        }
        else
        {
            for (int i = 0; i < sides; i++)
            {
                float a = i * MathF.Tau / sides;
                vertices.Add(new Vector3(.5f * MathF.Cos(a), -.5f, .5f * MathF.Sin(a)));
            }
            if (collider.Shape == IkColliderShape.Cylinder)
                vertices.AddRange(vertices.Select(v => v with { Y = .5f }).ToArray());
            else vertices.Add(new Vector3(0, .5f, 0));
            faces.Add(Enumerable.Range(0, sides).ToArray());
            if (collider.Shape == IkColliderShape.Cylinder)
                faces.Add(Enumerable.Range(sides, sides).ToArray());
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                faces.Add(collider.Shape == IkColliderShape.Cylinder
                    ? new[] { i, next, next + sides, i + sides } : new[] { i, next, sides });
                // Only the sharp rim is outlined, never the curved surface's tessellation.
                edges.Add((i, next));
                if (collider.Shape == IkColliderShape.Cylinder) edges.Add((i + sides, next + sides));
            }
        }
        var transform = collider.Transform;
        Vector3 World(Vector3 v) => Vector3.Transform(v * transform.Scale, transform.Rotation) + transform.Position;
        Vertices = vertices.Select(World).ToArray();
        Faces = faces.ToArray();
        Edges = edges.ToArray();
        var planes = new List<Plane>();
        // A finite plane has two coincident faces plus four bounded edges.
        if (collider.Shape == IkColliderShape.Plane)
        {
            var up = Vector3.Transform(Vector3.UnitY, transform.Rotation);
            planes.Add(new Plane(up, -Vector3.Dot(up, transform.Position)));
            planes.Add(new Plane(-up, Vector3.Dot(up, transform.Position)));
            for (int i = 0; i < 4; i++)
            {
                var middle = (Vertices[i] + Vertices[(i + 1) % 4]) * .5f;
                var normal = Vector3.Normalize(middle - transform.Position);
                planes.Add(new Plane(normal, -Vector3.Dot(normal, middle)));
            }
        }
        else foreach (var face in Faces)
        {
            var a = Vertices[face[0]];
            var normal = Vector3.Normalize(Vector3.Cross(Vertices[face[1]] - a, Vertices[face[2]] - a));
            if (Vector3.Dot(normal, a - transform.Position) < 0) normal = -normal;
            planes.Add(new Plane(normal, -Vector3.Dot(normal, a)));
        }
        _planes = planes.ToArray();
    }

    /// <summary>Clip the whole thick segment against the convex shape, not just its endpoints.</summary>
    public bool Contact(Vector3 a, Vector3 b, float radius, out float t, out Vector3 correction)
    {
        // A zero-width link still collides with a plane instead of silently
        // crossing its zero-volume surface. This skin is below visible precision.
        radius = MathF.Max(radius, .0001f);
        float enter = 0, exit = 1;
        foreach (var plane in _planes)
        {
            float start = Plane.DotCoordinate(plane, a) - radius;
            float delta = Vector3.Dot(plane.Normal, b - a);
            if (MathF.Abs(delta) < 1e-8f) { if (start > 0) { t = 0; correction = default; return false; } }
            else if (delta < 0) enter = MathF.Max(enter, -start / delta);
            else exit = MathF.Min(exit, -start / delta);
            if (enter > exit) { t = 0; correction = default; return false; }
        }
        t = (enter + exit) * .5f;
        var point = Vector3.Lerp(a, b, t);
        float nearest = float.NegativeInfinity;
        Vector3 normal = default;
        foreach (var plane in _planes)
        {
            float distance = Plane.DotCoordinate(plane, point);
            if (distance > nearest) { nearest = distance; normal = plane.Normal; }
        }
        correction = normal * MathF.Max(0, radius - nearest);
        return correction.LengthSquared() > 1e-12f;
    }

    internal Plane? SupportingFace(Vector3 a, Vector3 b, float radius)
    {
        // Both anchors outside the same face define a coherent route. Without
        // this, neighbouring links can independently choose opposite box faces.
        Plane? result = null;
        float nearest = float.PositiveInfinity;
        foreach (var plane in _planes)
        {
            float da = Plane.DotCoordinate(plane, a) - radius;
            float db = Plane.DotCoordinate(plane, b) - radius;
            if (da < 0 || db < 0 || da + db >= nearest) continue;
            nearest = da + db;
            result = plane;
        }
        return result;
    }

    internal void ProjectSegment(ref Vector3 a, ref Vector3 b, bool pinA, bool pinB,
        float radius, Plane? support)
    {
        if (!Contact(a, b, radius, out _, out _)) return;
        radius = MathF.Max(radius, .0001f);
        Vector3 moveA = default, moveB = default;
        float best = float.PositiveInfinity;
        for (int index = 0; index < (support.HasValue ? 1 : _planes.Length); index++)
        {
            var plane = support ?? _planes[index];
            float da = MathF.Max(0, radius - Plane.DotCoordinate(plane, a));
            float db = MathF.Max(0, radius - Plane.DotCoordinate(plane, b));
            if ((pinA && da > 1e-6f) || (pinB && db > 1e-6f)) continue;
            float cost = da * da + db * db;
            if (cost >= best) continue;
            best = cost;
            moveA = pinA ? default : plane.Normal * da;
            moveB = pinB ? default : plane.Normal * db;
        }
        // Move the whole link outside one face, not just its intersection midpoint.
        a += moveA;
        b += moveB;
    }
}

public static class IkCollisionSolver
{
    public static void Solve(Vector3[] positions, int handle, IReadOnlyList<ColliderGeometry> colliders,
        float radius, int iterations, Vector3? down = null)
    {
        if (colliders.Count == 0 || positions.Length < 2) return;
        var original = positions.ToArray();
        var lengths = Enumerable.Range(0, positions.Length - 1)
            .Select(i => Vector3.Distance(positions[i], positions[i + 1])).ToArray();
        bool Pinned(int i) => i == 0 || i == handle || i == positions.Length - 1;
        var supports = colliders.Select(c => new[] {
            c.SupportingFace(positions[0], positions[handle], radius),
            c.SupportingFace(positions[handle], positions[^1], radius) }).ToArray();
        bool intersects = colliders.Any(c => Enumerable.Range(0, lengths.Length)
            .Any(i => c.Contact(positions[i], positions[i + 1], radius, out _, out _)));
        if (!intersects) return;
        int passes = Math.Clamp(iterations, 1, 60);
        bool LengthsValid()
        {
            for (int i = 0; i < lengths.Length; i++)
                if (!TransformMath.IsFinite(positions[i]) || !TransformMath.IsFinite(positions[i + 1]) ||
                    MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - lengths[i]) > .0001f + lengths[i] * .001f)
                    return false;
            return true;
        }
        bool Clear(int first, int last)
        {
            foreach (var collider in colliders)
                for (int i = first; i < last; i++)
                    if (collider.Contact(positions[i], positions[i + 1], radius, out _, out var correction)
                        && correction.LengthSquared() > 1e-8f) return false;
            return true;
        }
        // Longer spans need more length propagation. The nominal iteration
        // budget is not permission to abandon a still-penetrating solution.
        for (int pass = 0; pass < Math.Max(passes, positions.Length * 8); pass++)
        {
            if (down is { } gravity && pass < passes / 2)
                for (int i = 1; i < positions.Length - 1; i++)
                    if (!Pinned(i)) positions[i] += gravity * MathF.Min(lengths[i - 1], lengths[i]) * .05f;
            if (pass == 4)
                for (int c = 0; c < colliders.Count; c++)
                    for (int side = 0; side < 2; side++)
                    {
                        int first = side == 0 ? 0 : handle, last = side == 0 ? handle : positions.Length - 1;
                        if (last - first < 2 || supports[c][side] is not { } support) continue;
                        float error = 0;
                        for (int i = first; i < last; i++)
                            error += MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - lengths[i]);
                        if (error < .001f) continue;
                        var across = Vector3.Cross(support.Normal, positions[last] - positions[first]);
                        if (across.LengthSquared() < 1e-10f) continue;
                        across = Vector3.Normalize(across);
                        bool planar = true;
                        for (int i = first + 1; i < last; i++)
                            planar &= MathF.Abs(Vector3.Dot(positions[i] - positions[first], across)) < .0001f;
                        if (!planar) continue;
                        // A compressed planar span needs one coherent out-of-plane
                        // bend to escape a symmetric deadlock, not a different kick
                        // at every contact (which creates alternating little arches).
                        for (int i = first + 1; i < last; i++)
                            positions[i] += across * error * MathF.Sin(MathF.PI * (i - first) / (last - first));
                    }
            // Contacts follow every distance sweep, so restoring link lengths
            // cannot repeatedly pull a settled span back through the surface.
            for (int sweep = 0; sweep < 8; sweep++)
            {
                for (int step = 0; step < lengths.Length; step++)
                {
                    int i = (sweep & 1) == 0 ? step : lengths.Length - 1 - step;
                    var delta = positions[i + 1] - positions[i];
                    float length = delta.Length();
                    bool moveA = !Pinned(i) && (Pinned(i + 1) || (sweep & 1) != 0);
                    bool moveB = !Pinned(i + 1) && !moveA;
                    if (length < 1e-8f || (!moveA && !moveB)) continue;
                    var correction = delta * ((length - lengths[i]) / length);
                    if (moveA) positions[i] += correction;
                    if (moveB) positions[i + 1] -= correction;
                }
                for (int c = 0; c < colliders.Count; c++)
                    for (int step = 0; step < lengths.Length; step++)
                    {
                        int i = (sweep & 1) == 0 ? step : lengths.Length - 1 - step;
                        colliders[c].ProjectSegment(ref positions[i], ref positions[i + 1],
                            Pinned(i), Pinned(i + 1), radius, supports[c][i < handle ? 0 : 1]);
                    }
            }
            if (pass >= passes && LengthsValid() && Clear(0, lengths.Length)) return;
        }
        if (LengthsValid() && Clear(0, lengths.Length)) return;

        // Before conceding to the pinned reach limits, try turning each authored
        // span around its anchor axis. This preserves every length and pin exactly
        // and can escape a blocked local minimum without returning through the box.
        original.CopyTo(positions, 0);
        for (int side = 0; side < 2; side++)
        {
            int first = side == 0 ? 0 : handle, last = side == 0 ? handle : positions.Length - 1;
            if (last - first < 2 || Clear(first, last)) continue;
            var axis = positions[last] - positions[first];
            if (axis.LengthSquared() < 1e-10f) continue;
            axis = Vector3.Normalize(axis);
            bool cleared = false;
            for (int step = 1; step <= 24 && !cleared; step++)
            {
                float angle = ((step + 1) / 2) * MathF.PI / 12 * (step % 2 == 0 ? -1 : 1);
                var turn = Quaternion.CreateFromAxisAngle(axis, angle);
                for (int i = first + 1; i < last; i++)
                    positions[i] = original[first] + Vector3.Transform(original[i] - original[first], turn);
                cleared = Clear(first, last);
            }
            if (!cleared)
                for (int i = first + 1; i < last; i++) positions[i] = original[i];
        }
    }
}
