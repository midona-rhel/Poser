using System.Numerics;
using Poser.Domain.Posing;
using Poser.Game.Posing;

namespace Poser.Game.Tests;

public class BepuIkCollisionTests(Xunit.ITestOutputHelper output)
{
    [Fact]
    public void ObstacleAdditionResizeReorderingAndRemovalKeepTheRopeSupported()
    {
        var floor = new IkCollider { Shape = IkColliderShape.Box,
            Transform = new(new(0, .4f, 0), Quaternion.Identity, new(8, 1, 8)) };
        var remote = new IkCollider { Shape = IkColliderShape.Sphere,
            Transform = new(new(10, 0, 0), Quaternion.Identity, Vector3.One) };
        var rest = Enumerable.Range(0, 21).Select(i => new Vector3(-1.5f + i * .15f, 1.4f + .7f * MathF.Sin(i * MathF.PI / 20), 0)).ToArray();
        using var state = new BepuIkCollisionState();
        foreach (var obstacles in new IkCollider[][] {
            [floor], [floor, remote], [floor, remote with { Transform = remote.Transform with { Scale = new(2) } }],
            [remote, floor], [floor], [floor with { Transform = floor.Transform with { Scale = new(9, 1, 9) } }] })
        {
            var geometry = obstacles.Select(p => new ColliderGeometry(p)).ToArray();
            var positions = rest.ToArray();
            for (int frame = 0; frame < 240; frame++)
            {
                positions = rest.ToArray();
                state.Solve(positions, 20, geometry, .04f, -Vector3.UnitY, rest);
            }
            Assert.InRange(positions.Min(p => p.Y), .935f, .955f);
            for (int i = 0; i < 20; i++)
                Assert.InRange(MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - Vector3.Distance(rest[i], rest[i + 1])), 0, .005f);
        }
    }

    [Theory]
    [InlineData(true, true, 50)]
    public void CylinderWrapSurvivesTargetAndColliderDragging(bool rope, bool moveCollider, int links)
    {
        var source = Enumerable.Range(0, links + 1).Select(i =>
        {
            float angle = MathF.PI + i * MathF.PI / links;
            return new Vector3(1.5f * MathF.Cos(angle), 0, 1.5f * MathF.Sin(angle));
        }).ToArray();
        using var state = new BepuIkCollisionState();
        var previous = source.ToArray();
        var timer = new System.Diagnostics.Stopwatch();
        for (int frame = 0; frame <= 150; frame++)
        {
            float angle = frame * MathF.PI / 180;
            var geometry = new ColliderGeometry(new() { Shape = IkColliderShape.Cylinder,
                Transform = new(new(moveCollider ? .1f * MathF.Sin(angle) : 0, 0, 0), Quaternion.Identity, new(1, 10, 1)) });
            var target = new Vector3(1.5f * MathF.Cos(angle), 0, 1.5f * MathF.Sin(angle));
            var positions = source.ToArray();
            positions[^1] = target;
            if (frame > 0) timer.Start();
            state.Solve(positions, links, [geometry], .04f, rope ? -Vector3.UnitY : null, source);
            timer.Stop();
            Assert.True(Vector3.Distance(source[0], positions[0]) < .002f, $"Root moved at {frame}: {positions[0]}");
            if (frame <= 135)
                Assert.True(Vector3.Distance(positions[^1], target) < .02f, $"Target lag at {frame}: {Vector3.Distance(positions[^1], target)}");
            for (int i = 0; i < links; i++)
            {
                Assert.True(Vector3.Distance(previous[i], positions[i]) < .15f, $"Jump at {frame}/{i}");
                float stretch = MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - Vector3.Distance(source[i], source[i + 1]));
                Assert.True(stretch < .002f, $"Stretch {stretch} at {frame}/{i}");
                if (geometry.Contact(positions[i], positions[i + 1], .04f, out _, out var correction))
                    Assert.True(correction.Length() < .002f, $"Penetration {correction.Length()} at {frame}/{i}");
            }
            previous = positions;
        }
        Assert.True(previous.Any(p => p.Z < -.54f), $"Lowest wrapped point: {previous.Min(p => p.Z)}");
        output.WriteLine($"{links} links, Rope={rope}: {timer.Elapsed.TotalMilliseconds / 150:F3} ms/solve (headless, warm)");
    }

    [Theory]
    [InlineData(IkColliderShape.Cone)]
    public void ThickChainRespectsTransformedShapesAndCanBeRecreated(IkColliderShape shape)
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
        var origin = new Vector3(14, 6, -8);
        var source = Enumerable.Range(0, 21).Select(i =>
            origin + Vector3.Transform(new Vector3(-1.5f + i * .15f, .8f + MathF.Sin(i * MathF.PI / 20), 0), rotation)).ToArray();
        var collider = new IkCollider { Shape = shape, Transform = new(origin, rotation, new(1, 1, 1.4f)) };
        using var state = new BepuIkCollisionState();
        for (int run = 0; run < 3; run++)
        {
            if (run == 2) state.Reset();
            var geometry = new ColliderGeometry(collider);
            float radius = run == 0 ? .04f : .12f;
            var positions = source.ToArray();
            for (int frame = 0; frame < 120; frame++)
            {
                positions = source.ToArray();
                state.Solve(positions, 20, [geometry], radius, -Vector3.UnitY, source);
            }
            for (int i = 0; i < 20; i++)
            {
                Assert.InRange(MathF.Abs(Vector3.Distance(positions[i], positions[i + 1]) - Vector3.Distance(source[i], source[i + 1])), 0, .002f);
                AssertCapsuleClear(geometry, positions[i], positions[i + 1], radius);
            }
        }
    }

    private static void AssertCapsuleClear(ColliderGeometry geometry, Vector3 a, Vector3 b, float radius)
    {
        // Inflating every face plane creates square corners, not a capsule's
        // rounded contact. Check actual mesh distance independently of Bepu.
        for (int sample = 0; sample <= 128; sample++)
        {
            var point = Vector3.Lerp(a, b, sample / 128f);
            Assert.False(geometry.Contact(point, point, 0, out _, out var inside) && inside.Length() > .002f,
                $"Centerline inside {geometry.Description.Shape}: {point}");
            float distance = float.MaxValue;
            foreach (var face in geometry.Faces)
                for (int i = 1; i < face.Length - 1; i++)
                    distance = MathF.Min(distance, TriangleDistanceSquared(point, geometry.Vertices[face[0]],
                        geometry.Vertices[face[i]], geometry.Vertices[face[i + 1]]));
            Assert.True(distance >= (radius - .002f) * (radius - .002f),
                $"{geometry.Description.Shape} radius {radius}, surface distance {MathF.Sqrt(distance)} at {point}");
        }
    }

    private static float TriangleDistanceSquared(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        static float Edge(Vector3 p, Vector3 v, Vector3 w)
        {
            var d = w - v;
            float t = d.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(p - v, d) / d.LengthSquared(), 0, 1) : 0;
            return Vector3.DistanceSquared(p, v + d * t);
        }
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() > 1e-12f)
        {
            normal = Vector3.Normalize(normal);
            var projected = point - normal * Vector3.Dot(normal, point - a);
            if (Vector3.Dot(Vector3.Cross(b - a, projected - a), normal) >= 0 &&
                Vector3.Dot(Vector3.Cross(c - b, projected - b), normal) >= 0 &&
                Vector3.Dot(Vector3.Cross(a - c, projected - c), normal) >= 0)
                return Vector3.DistanceSquared(point, projected);
        }
        return MathF.Min(Edge(point, a, b), MathF.Min(Edge(point, b, c), Edge(point, c, a)));
    }
}
