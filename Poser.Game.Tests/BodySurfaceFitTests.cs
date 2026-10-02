using System.Numerics;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Game.Posing;
using static Poser.Game.Posing.ActorColliderMeshBuilder;

namespace Poser.Game.Tests;

public class BodySurfaceFitTests
{
    internal sealed class Mesh
    {
        internal readonly List<Vector3> Vertices = [];
        internal readonly List<int> Indices = [];
        internal readonly List<BoneWeight[]> Weights = [];
        internal void Add(IkCollider collider, BoneWeight[] weights, Matrix4x4? deformation = null)
        {
            int start = Vertices.Count;
            var geometry = new ColliderGeometry(collider, 32);
            Vertices.AddRange(geometry.Vertices.Select(v => Vector3.Transform(v, deformation ?? Matrix4x4.Identity)));
            Weights.AddRange(geometry.Vertices.Select(_ => weights));
            foreach (var face in geometry.Faces)
                for (int i = 1; i + 1 < face.Length; i++)
                    Indices.AddRange([start + face[0], start + face[i], start + face[i + 1]]);
        }
        internal List<BodySurfaceFit.Sample> Surface()
        {
            var area = new float[Vertices.Count];
            for (int i = 0; i < Indices.Count; i += 3)
            {
                int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
                float share = Vector3.Cross(Vertices[b] - Vertices[a], Vertices[c] - Vertices[a]).Length() / 6;
                area[a] += share; area[b] += share; area[c] += share;
            }
            return Vertices.Select((p, i) => new BodySurfaceFit.Sample(p, area[i])).Where(p => p.Area > 0).ToList();
        }
    }

    internal static IkCollider Capsule(Vector3 center, float radius, float stem, Quaternion? rotation = null) => new()
    {
        Shape = IkColliderShape.Capsule,
        Transform = new(center, rotation ?? Quaternion.Identity, IkCollider.CapsuleScale(radius, stem)),
    };

    [Fact]
    public void FitsAnOffsetLimbSurfaceRatherThanCenteringItOnTheBone()
    {
        var mesh = new Mesh();
        var expected = Capsule(new(.12f, .5f, -.04f), .2f, .7f);
        mesh.Add(expected, [new("j_ude_a_l", 1)]);
        var joints = new Dictionary<string, ActorBodyColliderBuilder.Joint>
        {
            ["j_ude_a_l"] = new(Vector3.Zero, null),
            ["j_ude_b_l"] = new(Vector3.UnitY, "j_ude_a_l"),
        };
        var fit = ActorBodyColliderBuilder.Fit(joints, mesh.Vertices, mesh.Indices, mesh.Weights);
        var arm = Assert.Single(fit, p => p.Name == "Left upper arm").Collider;
        Assert.InRange(Vector3.Distance(expected.Transform.Position, arm.Transform.Position), 0, .015f);
        Assert.InRange(arm.RoundDimensions().Radius, .19f, .21f);
        Assert.InRange(arm.RoundDimensions().Stem, .67f, .73f);
        Assert.True(mesh.Vertices.Max(v => MathF.Abs(BodySurfaceFit.Distance(arm, v))) < .02f);
    }

    [Theory]
    [InlineData(.6f)]
    [InlineData(1f)]
    [InlineData(2.4f)]
    public void ActorRotationAndUniformScalingDoNotChangeProportions(float scale)
    {
        var mesh = new Mesh();
        mesh.Add(Capsule(new(.12f, .5f, -.04f), .2f, .7f), []);
        var surface = mesh.Surface();
        var baseline = BodySurfaceFit.Fit(surface, Vector3.Zero, Quaternion.Identity, Vector3.UnitY, false, false);
        var rotation = Quaternion.CreateFromYawPitchRoll(.93f, -.74f, 1.4f);
        var translation = new Vector3(6, -2, 1);
        var moved = surface.Select(p => new BodySurfaceFit.Sample(translation + Vector3.Transform(p.Position * scale, rotation), p.Area * scale * scale)).ToArray();
        var fit = BodySurfaceFit.Fit(moved, translation, rotation, Vector3.Transform(Vector3.UnitY, rotation), false, false);
        Assert.InRange(Vector3.Distance(fit.Transform.Position, translation + Vector3.Transform(baseline.Transform.Position * scale, rotation)), 0, .003f * scale);
        Assert.InRange(MathF.Abs(fit.RoundDimensions().Radius - baseline.RoundDimensions().Radius * scale), 0, .003f * scale);
        Assert.InRange(MathF.Abs(fit.RoundDimensions().Stem - baseline.RoundDimensions().Stem * scale), 0, .003f * scale);
    }

    [Fact]
    public void NonuniformFinalBoneScaleIsMeasuredOnceAndLocalAttachmentDoesNotApplyItAgain()
    {
        var mesh = new Mesh();
        // Final C+ output can include uneven model-space scaling as well as
        // changed joint translations; feed that geometry, not the saved profile.
        var scale = Matrix4x4.CreateScale(1.6f, 1.3f, 1.6f);
        mesh.Add(Capsule(new(.1f, .5f, 0), .2f, .7f), [], scale);
        var fit = BodySurfaceFit.Fit(mesh.Surface(), Vector3.Zero, Quaternion.Identity, Vector3.UnitY, false, false);
        Assert.InRange(fit.RoundDimensions().Radius, .30f, .34f);
        Assert.InRange(fit.Transform.Position.X, .15f, .17f);
        Assert.InRange(fit.Transform.Position.Y, .63f, .67f);
        var parent = new PoseTransform(new(3, 4, 5), Quaternion.CreateFromYawPitchRoll(.4f, .1f, -.2f), new(1.6f, 1.3f, 1.6f));
        var local = TransformParent.Local(fit.Transform, parent);
        var restored = TransformParent.World(local, parent);
        Assert.InRange(Vector3.Distance(restored.Position, fit.Transform.Position), 0, .00001f);
        Assert.Equal(fit.Transform.Scale, restored.Scale);
    }

    [Fact]
    public void TorsoAndHeadCanUseTheirSurfaceLongAxisInsteadOfTheSpineDirection()
    {
        var mesh = new Mesh();
        mesh.Add(Capsule(new(0, .2f, 0), .18f, .5f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), []);
        var fit = BodySurfaceFit.Fit(mesh.Surface(), Vector3.Zero, Quaternion.Identity, Vector3.UnitY, false, true);
        Assert.True(MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitY, fit.Transform.Rotation), Vector3.UnitX)) > .98f);
        Assert.InRange(fit.RoundDimensions().Radius, .16f, .20f);
        Assert.True(mesh.Vertices.Max(v => MathF.Abs(BodySurfaceFit.Distance(fit, v))) < .025f);
    }

    [Fact]
    public void DenseInternalDetailAndTinyOutliersDoNotDetermineOuterBodySize()
    {
        var mesh = new Mesh();
        mesh.Add(Capsule(new(0, .5f, 0), .2f, .7f), []);
        var clean = mesh.Surface();
        var noisy = clean.ToList();
        // Many tiny details should not outweigh the actual skin surface.
        for (int i = 0; i < 10000; i++) noisy.Add(new(new(0, .5f, 0), 1e-9f));
        noisy.Add(new(new(10, .5f, 0), 1e-9f));
        var fit = BodySurfaceFit.Fit(noisy, Vector3.Zero, Quaternion.Identity, Vector3.UnitY, false, false);
        Assert.InRange(fit.RoundDimensions().Radius, .19f, .21f);
        Assert.InRange(fit.RoundDimensions().Stem, .67f, .73f);
    }

    [Fact]
    public void SplitWeightsAcrossHelperBonesStillOwnTheWholeLimb()
    {
        var mesh = new Mesh();
        mesh.Add(Capsule(new(0, .5f, 0), .2f, .7f),
            [new("j_ude_a_l", .4f), new("helper1", .35f), new("helper2", .25f)]);
        var joints = new Dictionary<string, ActorBodyColliderBuilder.Joint>
        {
            ["j_ude_a_l"] = new(Vector3.Zero, null), ["j_ude_b_l"] = new(Vector3.UnitY, "j_ude_a_l"),
            ["helper1"] = new(new(0, .3f, 0), "j_ude_a_l"), ["helper2"] = new(new(0, .7f, 0), "helper1"),
        };
        var arm = Assert.Single(ActorBodyColliderBuilder.Fit(joints, mesh.Vertices, mesh.Indices, mesh.Weights), p => p.Name == "Left upper arm");
        Assert.InRange(arm.Collider.RoundDimensions().Radius, .19f, .21f);
    }

    [Fact]
    public void TailRegionsDoNotInflateTheWaistAndFollowTheirOwnBones()
    {
        var mesh = new Mesh();
        mesh.Add(Capsule(new(0, .5f, 0), .2f, .7f), [new("j_kosi", 1)]);
        mesh.Add(Capsule(new(0, 0, -.5f), .07f, .8f, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)), [new("n_sippo_a", 1)]);
        mesh.Add(Capsule(new(0, 0, -1.2f), .035f, .25f, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)), [new("n_sippo_b", 1)]);
        var joints = new Dictionary<string, ActorBodyColliderBuilder.Joint>
        {
            ["j_kosi"] = new(Vector3.Zero, null), ["j_sebo_a"] = new(Vector3.UnitY, "j_kosi"),
            ["n_sippo_a"] = new(Vector3.Zero, "j_kosi"), ["n_sippo_b"] = new(new(0, 0, -1), "n_sippo_a"),
        };
        var fit = ActorBodyColliderBuilder.Fit(joints, mesh.Vertices, mesh.Indices, mesh.Weights);
        Assert.Equal(3, fit.Length);
        Assert.InRange(fit.Single(p => p.Name == "Waist").Collider.RoundDimensions().Radius, .18f, .22f);
        Assert.Equal(new[] { "n_sippo_a", "n_sippo_b" }, fit.Where(p => p.Name.StartsWith("Tail")).Select(p => p.BoneName));
        Assert.InRange(fit.Single(p => p.BoneName == "n_sippo_a").Collider.RoundDimensions().Radius, .06f, .08f);
    }

    [Fact]
    public void UnknownOrOverBudgetSkeletonRefusesWithoutInventingBodyParts()
    {
        Assert.Throws<InvalidDataException>(() => ActorBodyColliderBuilder.BodyBones(new Dictionary<string, ActorBodyColliderBuilder.Joint>
        { ["root"] = new(Vector3.Zero, null), ["arbitrary"] = new(Vector3.UnitY, "root") }));
        var joints = new Dictionary<string, ActorBodyColliderBuilder.Joint>
        { ["j_kosi"] = new(Vector3.Zero, null), ["j_sebo_a"] = new(Vector3.UnitY, "j_kosi") };
        for (int i = 0; i < 100; i++) joints[$"n_sippo_{i:D3}"] = new(new(0, 0, i), i == 0 ? "j_kosi" : $"n_sippo_{i - 1:D3}");
        Assert.Throws<InvalidDataException>(() => ActorBodyColliderBuilder.BodyBones(joints));
    }

    [Fact]
    public void AnalyticPhysicsInputsDoNotAllocateTessellatedSurfaces()
    {
        var source = Capsule(Vector3.Zero, .2f, 1);
        _ = new ColliderGeometry(source); // JIT before measurement.
        float sum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            var geometry = new ColliderGeometry(source with { Transform = source.Transform with { Position = new(i, 0, 0) } });
            sum += geometry.Description.RoundDimensions().Radius;
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 500_000);
        Assert.InRange(sum, 199.99f, 200.01f);
        // The same lazy input remains usable by the overlay/contact path.
        var overlay = new ColliderGeometry(source);
        Assert.NotEmpty(overlay.Vertices);
        Assert.NotEmpty(overlay.Faces);
        Assert.True(overlay.Contact(new(-1, 0, 0), new(1, 0, 0), .05f, out _, out _));
    }
}
