using System.Numerics;
using System.Text;
using Poser.Game.Posing;

namespace Poser.Game.Tests;

public class ActorColliderDeformationTests
{
    [Fact]
    public void SiblingModelIsConvertedThroughCommonAncestorBeforeSkinning()
    {
        var source = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(5, 6, 7);
        var target = Matrix4x4.CreateRotationZ(.7f) * Matrix4x4.CreateTranslation(-3, 2, 1);
        var pbd = File((201, -1, null), (801, 0, source), (1401, 0, target));
        var convert = ActorColliderDeformation.Read(pbd, 1401, 801)["arm"];
        var commonPoint = new Vector3(2, 4, 6);
        var posed = Matrix4x4.CreateRotationX(.4f) * Matrix4x4.CreateTranslation(10, 20, 30);
        Near(Vector3.Transform(Vector3.Transform(commonPoint, source), convert * posed),
            Vector3.Transform(commonPoint, target * posed));
        var reverse = ActorColliderDeformation.Read(pbd, 801, 1401)["arm"];
        Near(Vector3.Transform(commonPoint, convert * reverse), commonPoint);
    }

    [Fact]
    public void MultiLevelAncestorCompositionIsPreserved()
    {
        var parent = Matrix4x4.CreateScale(2, 3, 4);
        var child = Matrix4x4.CreateTranslation(5, 6, 7);
        var pbd = File((101, -1, null), (201, 0, parent), (1401, 1, child));
        var point = new Vector3(2, 4, 6);
        Near(Vector3.Transform(point, ActorColliderDeformation.Read(pbd, 1401, 101)["arm"]),
            Vector3.Transform(point, child * parent));
        Near(Vector3.Transform(Vector3.Transform(point, child * parent),
            ActorColliderDeformation.Read(pbd, 101, 1401)["arm"]), point);
    }

    [Fact]
    public void EmptySourceDeformerUsesIdentity()
    {
        var target = Matrix4x4.CreateTranslation(2, 3, 4);
        var pbd = File((201, -1, null), (801, 0, null), (1401, 0, target));
        Assert.Equal(target, ActorColliderDeformation.Read(pbd, 1401, 801)["arm"]);
        Assert.Empty(ActorColliderDeformation.Read(pbd, 1401, 1401));
        Assert.Empty(ActorColliderDeformation.Read(pbd, 1401, 0));
    }

    [Fact]
    public void InvalidConversionStillFailsInsteadOfProducingWrongColliders()
    {
        Assert.Throws<InvalidDataException>(() => ActorColliderDeformation.Read(
            File((201, -1, null), (801, 0, Matrix4x4.CreateScale(0)), (1401, 0, Matrix4x4.Identity)), 1401, 801));
        Assert.Throws<InvalidDataException>(() => ActorColliderDeformation.Read(
            File((801, -1, null), (1401, -1, null)), 1401, 801));
        Assert.Throws<InvalidDataException>(() => ActorColliderDeformation.Read(
            File((801, 1, null), (1401, 0, null)), 1401, 801));
        Assert.Throws<InvalidDataException>(() => ActorColliderDeformation.Read(
            File((1401, -1, null)), 1401, 801));
    }

    private static void Near(Vector3 actual, Vector3 expected)
        => Assert.True(Vector3.Distance(actual, expected) < .0001f, $"Expected {expected}, got {actual}");

    private static byte[] File(params (ushort Race, short Parent, Matrix4x4? Matrix)[] entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            writer.Write(entries[i].Race); writer.Write((short)i); writer.Write(0); writer.Write(1f);
        }
        for (int i = 0; i < entries.Length; i++)
        {
            writer.Write(entries[i].Parent); writer.Write((short)-1); writer.Write((short)-1); writer.Write((short)i);
        }
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Matrix is not { } m) continue;
            long offset = stream.Position;
            stream.Position = 4 + i * 12 + 4;
            writer.Write((int)offset);
            stream.Position = offset;
            writer.Write(1); writer.Write((ushort)56); writer.Write((ushort)0);
            foreach (float value in new[] { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 })
                writer.Write(value);
            writer.Write(Encoding.UTF8.GetBytes("arm\0"));
        }
        return stream.ToArray();
    }
}
