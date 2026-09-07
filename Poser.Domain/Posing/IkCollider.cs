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
    public IkCollider Description { get; }
    public Vector3[] Vertices { get; }
    public int[][] Faces { get; }
    public (int A, int B)[] Edges { get; }
    private readonly Plane[] _planes;

    public ColliderGeometry(IkCollider collider, int sides = 32)
    {
        Description = collider;
        sides = Math.Clamp(sides, 8, 64);
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

}

/// <summary>Owned continuation of one live chain, independent of the physics backend.</summary>
public interface IIkCollisionState : IDisposable
{
    void Solve(Vector3[] positions, int handle, IReadOnlyList<ColliderGeometry> colliders,
        float radius, Vector3? down, IReadOnlyList<Vector3> restPose);
    Quaternion ResolveRotation(int link, Vector3 authoredDirection, Vector3 solvedDirection, Quaternion authoredRotation);
    void Reset();
}
