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
        int count = Count(0), current = -1;
        for (int i = 0; i < count; i++)
            if (UShort(4 + i * 12) == skeletonRace) { current = i; break; }
        if (current < 0) throw new InvalidDataException("The actor's race is missing from its racial deformer.");
        for (int depth = 0; depth < count; depth++)
        {
            int entry = 4 + current * 12;
            if (UShort(entry) == modelRace) return result;
            int offset = Count(entry + 4);
            if (offset != 0)
            {
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
                    if (depth == 0) result[name] = matrix;
                    else if (result.TryGetValue(name, out var child)) result[name] = child * matrix;
                }
            }
            int tree = 4 + count * 12, node = Short(entry + 2);
            int parent = Short(tree + node * 8);
            if (parent < 0) break;
            current = Short(tree + parent * 8 + 6);
        }
        throw new InvalidDataException("The model's race is not an ancestor in the actor's racial deformer.");
    }
}
