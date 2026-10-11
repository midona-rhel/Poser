using System.Numerics;
using Poser.Domain.Posing;
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
        internal void Add(IkCollider collider, BoneWeight[] weights)
        {
            int start = Vertices.Count;
            var geometry = new ColliderGeometry(collider, 32);
            Vertices.AddRange(geometry.Vertices);
            Weights.AddRange(geometry.Vertices.Select(_ => weights));
            foreach (var face in geometry.Faces)
                for (int i = 1; i + 1 < face.Length; i++)
                    Indices.AddRange([start + face[0], start + face[i], start + face[i + 1]]);
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
}
