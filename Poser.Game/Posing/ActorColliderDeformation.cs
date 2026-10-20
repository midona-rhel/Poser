using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Poser.Game.Posing;

internal static class ActorColliderDeformation
{
    internal const string GamePath = "chara/xls/boneDeformer/human.pbd";

    // PBD layouts and race-tree composition follow Penumbra.GameData's PbdFile
    // and RacialDeformer. Its 3x4 column-vector matrices transpose into our
    // row-vector Matrix4x4, and act on model vertices BEFORE posed skinning.
    internal static Dictionary<string, Matrix4x4> Read(byte[] bytes, ushort skeletonRace, ushort modelRace)
    {
        var result = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        if (skeletonRace == modelRace || modelRace == 0) return result;
        int Count(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        short Short(int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset));
        ushort UShort(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        int count = Count(0);
        List<int> Ancestry(ushort race)
        {
            int current = -1;
            for (int i = 0; i < count; i++)
                if (UShort(4 + i * 12) == race) { current = i; break; }
            if (current < 0) throw new InvalidDataException($"Race {race:D4} is missing from the racial deformer.");
            var chain = new List<int>();
            while (true)
            {
                if ((uint)current >= (uint)count || chain.Contains(current))
                    throw new InvalidDataException("Invalid or cyclic racial deformer tree.");
                chain.Add(current);
                int node = Short(4 + current * 12 + 2);
                if ((uint)node >= (uint)count) throw new InvalidDataException("Invalid racial deformer node.");
                int parent = Short(4 + count * 12 + node * 8);
                if (parent < 0) return chain;
                if (parent >= count) throw new InvalidDataException("Invalid racial deformer parent.");
                current = Short(4 + count * 12 + parent * 8 + 6);
            }
        }
        var actorChain = Ancestry(skeletonRace);
        var modelChain = Ancestry(modelRace);
        int common = actorChain.FirstOrDefault(modelChain.Contains, -1);
        if (common < 0) throw new InvalidDataException("The races have no common racial deformer ancestor.");

        Dictionary<string, Matrix4x4> Compose(List<int> chain)
        {
            var composed = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            for (int depth = 0; depth < chain.Count && chain[depth] != common; depth++)
            {
                int offset = Count(4 + chain[depth] * 12 + 4);
                if (offset == 0) continue;
                int bones = Count(offset), matrices = offset + 4 + (bones + (bones & 1)) * 2;
                for (int b = 0; b < bones; b++)
                {
                    int nameOffset = offset + UShort(offset + 4 + b * 2);
                    var nameData = bytes.AsSpan(nameOffset);
                    var name = Encoding.UTF8.GetString(nameData[..nameData.IndexOf((byte)0)]);
                    int matrixOffset = matrices + b * 48;
                    float F(int i) => BitConverter.ToSingle(bytes, matrixOffset + i * 4);
                    var matrix = new Matrix4x4(F(0), F(4), F(8), 0, F(1), F(5), F(9), 0,
                        F(2), F(6), F(10), 0, F(3), F(7), F(11), 1);
                    if (depth == 0) composed[name] = matrix;
                    else if (composed.TryGetValue(name, out var child)) composed[name] = child * matrix;
                }
            }
            return composed;
        }
        var actorDeform = Compose(actorChain);
        var modelDeform = Compose(modelChain);
        // Both paths start in the common ancestor's model space. For row vectors,
        // undo the source model's deformation first, then apply the actor's.
        // Ancestor-only models retain the original path (an empty source map).
        foreach (var name in actorDeform.Keys.Union(modelDeform.Keys))
        {
            var source = modelDeform.GetValueOrDefault(name, Matrix4x4.Identity);
            if (!Matrix4x4.Invert(source, out var inverse))
                throw new InvalidDataException($"Race {modelRace:D4}, bone '{name}': singular racial deformation.");
            result[name] = inverse * actorDeform.GetValueOrDefault(name, Matrix4x4.Identity);
        }
        return result;
    }
}
