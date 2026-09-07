using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Lumina.Data.Parsing;
using Poser.Domain.Posing;
using Poser.Game.Posing;

namespace Poser.Game.Tests;

public class ActorColliderMeshTests
{
    [Fact]
    public void RacialDeformationMovesTheModelBeforePosedSkinning()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(2);
        w.Write((ushort)801); w.Write((short)0); w.Write(44); w.Write(1f);
        w.Write((ushort)201); w.Write((short)1); w.Write(0); w.Write(1f);
        foreach (short value in new short[] { 1, -1, -1, 0, -1, 0, -1, 1 }) w.Write(value);
        w.Write(1); w.Write((ushort)56); w.Write((ushort)0);
        foreach (float value in new float[] { 1, 0, 0, 2, 0, 1, 0, 0, 0, 0, 1, 0 }) w.Write(value);
        w.Write(Encoding.UTF8.GetBytes("arm\0"));
        var pbd = stream.ToArray();
        var deform = ActorColliderDeformation.Read(pbd, 801, 201)["arm"];
        var posed = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 3, 0);
        var vertices = new List<Vector3>();
        ActorColliderMeshBuilder.Append(Model(true), 1, 0, new Dictionary<string, Matrix4x4> { ["arm"] = deform * posed },
            Matrix4x4.Identity, Vector3.Zero, vertices, []);
        Assert.True(Vector3.Distance(vertices[1], new(0, 6, 0)) < .0001f);
        Assert.Empty(ActorColliderDeformation.Read(pbd, 801, 801));
    }

    [Fact]
    public void BodyFitUsesBoneLengthAndSkinWidthWithoutAccessoryOutlier()
    {
        var vertices = new List<Vector3>();
        for (int i = 0; i < 100; i++) vertices.Add(new(.2f * MathF.Cos(i), i / 100f, .2f * MathF.Sin(i)));
        vertices.Add(new(10, .5f, 0));
        var joints = new Dictionary<string, ActorBodyColliderBuilder.Joint> {
            ["j_ude_a_l"] = new(Vector3.Zero, null), ["j_ude_b_l"] = new(Vector3.UnitY, "j_ude_a_l") };
        var fitted = ActorBodyColliderBuilder.Fit(joints, vertices, Enumerable.Range(0, vertices.Count).ToArray(),
            Enumerable.Repeat<string?>("j_ude_a_l", vertices.Count).ToArray());
        var capsule = Assert.Single(fitted);
        Assert.Equal("Left upper arm", capsule.Name);
        Assert.Equal(IkColliderShape.Capsule, capsule.Collider.Shape);
        Assert.InRange(capsule.Collider.RoundDimensions().Radius, .1999f, .2001f);
        Assert.Equal(new Vector3(0, .5f, 0), capsule.Collider.Transform.Position);
        Assert.Equal(1f, capsule.Collider.Transform.Scale.Y);
        var restored = JsonSerializer.Deserialize<IkCollider>(JsonSerializer.Serialize(capsule.Collider, global::Poser.Files.SceneFile.JsonOptions),
            global::Poser.Files.SceneFile.JsonOptions)!;
        Assert.Equal(capsule.Collider, restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturesWeightedPoseAndEnabledShapeInBothModelVersions(bool v6)
    {
        var bytes = Model(v6);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var bones = new Dictionary<string, Matrix4x4>
        {
            ["arm"] = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 2, 0),
        };
        ActorColliderMeshBuilder.Append(bytes, 1, 1, bones, Matrix4x4.Identity, new(10, 0, 0), vertices, indices);
        Assert.Equal(new[] { 0, 1, 3 }, indices);
        Assert.True(Vector3.Distance(vertices[1], new(0, 3, 0)) < .0001f);
        Assert.True(Vector3.Distance(vertices[3], new(-2, 2, 0)) < .0001f);
        var captured = new IkCollider { Shape = IkColliderShape.Mesh, Mesh = new(vertices.ToArray(), indices.ToArray()) };
        bones["arm"] = Matrix4x4.CreateTranslation(100, 0, 0);
        var options = global::Poser.Files.SceneFile.JsonOptions;
        var restored = JsonSerializer.Deserialize<IkCollider>(JsonSerializer.Serialize(captured, options), options)!;
        Assert.Equal(captured.Mesh.Vertices, restored.Mesh!.Vertices);
        Assert.Equal(captured.Mesh.Indices, restored.Mesh.Indices);
        vertices.Clear(); indices.Clear();
        ActorColliderMeshBuilder.Append(bytes, 0, 0, bones, Matrix4x4.Identity, Vector3.Zero, vertices, indices);
        Assert.Empty(indices); // Attribute-disabled equipment must not become an invisible obstacle.
    }

    [Fact]
    public void CapturesEightPackedByteInfluencesIncludingTheLastBone()
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        ActorColliderMeshBuilder.Append(Model(true, true), 1, 0,
            new Dictionary<string, Matrix4x4>
            {
                ["arm"] = Matrix4x4.Identity,
                ["tip"] = Matrix4x4.CreateTranslation(8, 0, 0),
            }, Matrix4x4.Identity, Vector3.Zero, vertices, indices);
        Assert.Equal(new[] { 0, 1, 2 }, indices);
        Assert.True(Vector3.Distance(vertices[0], new(8f * 191 / 255, 0, 0)) < .0001f);
        Assert.True(Vector3.Distance(vertices[1], vertices[0] + Vector3.UnitX) < .0001f);
    }

    [Fact]
    public void UnmappedWeightedBoneRefusesInsteadOfCapturingBindPose()
    {
        Assert.Throws<InvalidDataException>(() => ActorColliderMeshBuilder.Append(Model(true), 1, 0,
            new Dictionary<string, Matrix4x4>(), Matrix4x4.Identity, Vector3.Zero, [], []));
    }

    // A complete minimal binary MDL: weighted triangle, attribute-gated submesh,
    // and one shape replacement. Exercises file parsing, not a mocked parsed model.
    private static byte[] Model(bool v6, bool eightWeights = false)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(new byte[68]); // file header patched after buffers are written
        void Element(byte offset, byte type, byte usage) => w.Write(new byte[] { 0, offset, type, usage, 0, 0, 0, 0 });
        Element(0, 2, 0);
        Element(12, eightWeights ? (byte)17 : (byte)8, 1);
        Element(eightWeights ? (byte)20 : (byte)16, eightWeights ? (byte)17 : (byte)5, 2);
        w.Write((byte)255); w.Write(new byte[17 * 8 - 25]);
        var names = Encoding.UTF8.GetBytes(eightWeights ? "arm\0attr\0shape\0tip\0" : "arm\0attr\0shape\0");
        w.Write(eightWeights ? (ushort)4 : (ushort)3); w.Write((ushort)0); w.Write((uint)names.Length); w.Write(names);
        Write(w, new MdlStructs.ModelHeader
        {
            MeshCount = 1, AttributeCount = 1, SubmeshCount = 1, BoneCount = eightWeights ? (ushort)2 : (ushort)1, BoneTableCount = 1,
            ShapeCount = 1, ShapeMeshCount = 1, ShapeValueCount = 1, LodCount = 1, Unknown7 = v6 ? (ushort)2 : (ushort)0,
        });
        Write(w, new MdlStructs.LodStruct { MeshCount = 1 });
        Write(w, new MdlStructs.LodStruct()); Write(w, new MdlStructs.LodStruct());
        w.Write((ushort)4); w.Write((ushort)0); w.Write((uint)3);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)0);
        byte stride = eightWeights ? (byte)28 : (byte)20;
        w.Write((uint)0); w.Write(new byte[12]); w.Write(new byte[] { stride, 0, 0, 1 });
        w.Write((uint)4); // attribute string offset
        Write(w, new MdlStructs.SubmeshStruct { IndexCount = 3, AttributeIndexMask = 1, BoneCount = 1 });
        w.Write((uint)0); // bone name offset
        if (eightWeights) w.Write(15u);
        if (v6) { w.Write((ushort)1); w.Write(eightWeights ? (ushort)2 : (ushort)1); w.Write((ushort)0); w.Write(eightWeights ? (ushort)1 : (ushort)0); }
        else { w.Write(new byte[128]); w.Write((uint)1); }
        w.Write((uint)9); w.Write(new byte[6]); w.Write((ushort)1); w.Write(new byte[4]);
        Write(w, new MdlStructs.ShapeMeshStruct { ShapeValueCount = 1 });
        Write(w, new MdlStructs.ShapeValueStruct { BaseIndicesIndex = 2, ReplacingVertexIndex = 3 });
        uint vertexOffset = (uint)stream.Position;
        foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitY * 2 })
        {
            w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
            if (eightWeights)
            {
                w.Write(new byte[] { 64, 64, 0, 0, 0, 0, 0, 127 });
                w.Write(new byte[] { 0, 1, 0, 0, 0, 0, 0, 1 });
            }
            else { w.Write(new byte[] { 255, 0, 0, 0 }); w.Write(new byte[4]); }
        }
        uint indexOffset = (uint)stream.Position;
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
        stream.Position = 0;
        w.Write(v6 ? 0x01000006u : 0x01000005u); w.Write(136u); w.Write(vertexOffset - 68 - 136);
        w.Write((ushort)1); w.Write((ushort)0);
        w.Write(vertexOffset); w.Write(new byte[8]); w.Write(indexOffset); w.Write(new byte[8]);
        w.Write((uint)(stride * 4)); w.Write(new byte[8]); w.Write(6u); w.Write(new byte[8]);
        w.Write(new byte[] { 1, 0, 0, 0 });
        return stream.ToArray();
    }

    private static void Write<T>(BinaryWriter writer, T value) where T : unmanaged
        => writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
}
