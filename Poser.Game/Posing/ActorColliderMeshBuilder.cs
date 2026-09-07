using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Extensions;
using static Lumina.Data.Parsing.MdlStructs;

namespace Poser.Game.Posing;

internal static class ActorColliderMeshBuilder
{
    internal static void Append(byte[] bytes, uint attributes, uint shapes,
        IReadOnlyDictionary<string, Matrix4x4> bones, Matrix4x4 world, Vector3 origin,
        List<Vector3> vertices, List<int> indices, List<string?>? influences = null,
        IReadOnlySet<string>? bodyBones = null)
    {
        var file = Read(bytes);
        Span<float> weightValues = stackalloc float[8];
        Span<float> indexValues = stackalloc float[8];
        string Name(uint offset)
        {
            var span = file.Strings.AsSpan(checked((int)offset));
            return Encoding.UTF8.GetString(span[..span.IndexOf((byte)0)]);
        }
        var boneNames = file.BoneNameOffsets.Select(Name).ToArray();
        var lod = file.Lods[0];
        var visibleMeshes = Enumerable.Range(lod.MeshIndex, lod.MeshCount).ToHashSet();
        if (file.ModelHeader.ExtraLodEnabled)
        {
            var extra = file.ExtraLods[0];
            visibleMeshes.UnionWith(Enumerable.Range(extra.GlassMeshIndex, extra.GlassMeshCount));
        }
        foreach (int meshIndex in visibleMeshes.Order())
        {
            var mesh = file.Meshes[meshIndex];
            var declarations = file.VertexDeclarations[meshIndex].VertexElements;
            var positions = declarations.Where(e => e.Usage == 0).ToArray();
            var weights = declarations.Where(e => e.Usage == 1).OrderBy(e => e.UsageIndex).ToArray();
            var blendIndices = declarations.Where(e => e.Usage == 2).OrderBy(e => e.UsageIndex).ToArray();
            if (positions.Length != 1 || weights.Length != blendIndices.Length)
                throw new InvalidDataException("Unsupported actor vertex layout.");
            var palette = mesh.BoneTableIndex < file.BoneTables.Length
                ? file.BoneTables[mesh.BoneTableIndex].BoneIndex : [];
            int start = vertices.Count;
            var included = new bool[mesh.VertexCount];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var p = ReadElement(bytes, file, mesh, positions[0], v);
                Vector3 position = new(p.X, p.Y, p.Z);
                Vector3 posed = default;
                float total = 0;
                float strongest = 0;
                string? influence = null;
                string? missingBone = null;
                for (int set = 0; set < weights.Length; set++)
                {
                    int count = ReadBlend(bytes, file, mesh, weights[set], v, weightValues);
                    if (count != ReadBlend(bytes, file, mesh, blendIndices[set], v, indexValues))
                        throw new InvalidDataException("Actor blend weights and indices have different lengths.");
                    for (int component = 0; component < count; component++)
                    {
                        float weight = weightValues[component];
                        if (weight <= 0) continue;
                        int localBone = checked((int)indexValues[component]);
                        if ((uint)localBone >= (uint)palette.Length || palette[localBone] >= boneNames.Length)
                            throw new InvalidDataException($"Mesh {meshIndex}, vertex {v}: invalid bone palette index {localBone}.");
                        if (!bones.TryGetValue(boneNames[palette[localBone]], out var skin))
                            missingBone = boneNames[palette[localBone]];
                        else posed += Vector3.Transform(position, skin) * weight;
                        if (weight > strongest) { strongest = weight; influence = boneNames[palette[localBone]]; }
                        total += weight;
                    }
                }
                // Classify before requiring transforms: hair/accessory vertices
                // are not body-fit samples. Retained vertices still need every
                // weighted transform; never substitute bind pose for missing data.
                included[v] = bodyBones == null || influence != null && bodyBones.Contains(influence);
                if (included[v] && missingBone != null)
                    throw new InvalidDataException($"Mesh {meshIndex}, vertex {v}: bone '{missingBone}' has no captured transform.");
                vertices.Add((total > 0 ? posed / total : Vector3.Transform(position, world)) - origin);
                influences?.Add(included[v] ? influence : null);
            }
            var meshIndices = new int[checked((int)mesh.IndexCount)];
            int indexOffset = checked((int)(file.FileHeader.IndexOffset[0] + mesh.StartIndex * 2));
            for (int i = 0; i < meshIndices.Length; i++)
                meshIndices[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(indexOffset + i * 2));
            // Shape keys replace indexed vertices (equipment hide/shape variants),
            // not just vertex positions. Capture only the currently enabled keys.
            for (int s = 0; s < file.Shapes.Length && s < 32; s++)
            {
                if ((shapes & (1u << s)) == 0) continue;
                var shape = file.Shapes[s];
                for (int sm = shape.ShapeMeshStartIndex[0]; sm < shape.ShapeMeshStartIndex[0] + shape.ShapeMeshCount[0]; sm++)
                {
                    var replacement = file.ShapeMeshes[sm];
                    if (replacement.MeshIndexOffset != mesh.StartIndex) continue;
                    for (uint value = replacement.ShapeValueOffset; value < replacement.ShapeValueOffset + replacement.ShapeValueCount; value++)
                    {
                        var pair = file.ShapeValues[value];
                        meshIndices[pair.BaseIndicesIndex] = pair.ReplacingVertexIndex;
                    }
                }
            }
            void AddRange(int offset, int count)
            {
                for (int i = offset; i + 2 < offset + count; i += 3)
                {
                    int a = start + meshIndices[i], b = start + meshIndices[i + 1], c = start + meshIndices[i + 2];
                    if ((uint)(a - start) >= mesh.VertexCount || (uint)(b - start) >= mesh.VertexCount || (uint)(c - start) >= mesh.VertexCount)
                        throw new InvalidDataException("An actor mesh triangle references a missing vertex.");
                    if (!included[a - start] || !included[b - start] || !included[c - start]) continue;
                    if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).LengthSquared() < 1e-16f) continue;
                    indices.Add(a); indices.Add(b); indices.Add(c);
                }
            }
            if (mesh.SubMeshCount == 0) AddRange(0, meshIndices.Length);
            else for (int sub = mesh.SubMeshIndex; sub < mesh.SubMeshIndex + mesh.SubMeshCount; sub++)
            {
                var part = file.Submeshes[sub];
                if ((part.AttributeIndexMask & attributes) != part.AttributeIndexMask) continue;
                AddRange(checked((int)(part.IndexOffset - mesh.StartIndex)), checked((int)part.IndexCount));
            }
        }
    }

    private static int ElementOffset(MdlFile file, MeshStruct mesh, VertexElement element, int vertex)
        => checked((int)(file.FileHeader.VertexOffset[0] + mesh.VertexBufferOffset[element.Stream]
            + vertex * mesh.VertexBufferStride[element.Stream] + element.Offset));

    private static int ReadBlend(byte[] bytes, MdlFile file, MeshStruct mesh, VertexElement element, int vertex, Span<float> values)
    {
        // MDL UShort4 blend attributes pack EIGHT bytes, not four ushort indices.
        // This matches Penumbra's MeshExporter.ReadVertexElement / VertexJoints8.
        if (element.Type == 17)
        {
            var packed = bytes.AsSpan(ElementOffset(file, mesh, element, vertex), 8);
            for (int i = 0; i < 8; i++) values[i] = element.Usage == 1 ? packed[i] / 255f : packed[i];
            return 8;
        }
        var value = ReadElement(bytes, file, mesh, element, vertex);
        for (int i = 0; i < 4; i++) values[i] = value[i];
        return 4;
    }

    private static Vector4 ReadElement(byte[] bytes, MdlFile file, MeshStruct mesh, VertexElement element, int vertex)
    {
        int offset = ElementOffset(file, mesh, element, vertex);
        var span = bytes.AsSpan(offset);
        float Single(int i) => BitConverter.ToSingle(bytes, offset + i * 4);
        float Half(int i) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + i * 2)));
        return element.Type switch
        {
            2 => new(Single(0), Single(1), Single(2), 0),
            3 => new(Single(0), Single(1), Single(2), Single(3)),
            5 => new(span[0], span[1], span[2], span[3]),
            8 => new Vector4(span[0], span[1], span[2], span[3]) / 255f,
            13 => new(Half(0), Half(1), 0, 0),
            14 => new(Half(0), Half(1), Half(2), Half(3)),
            _ => throw new InvalidDataException($"Unsupported actor vertex format {element.Type}."),
        };
    }

    // Lumina supplies the fixed MDL structures, but its MdlFile.LoadFile reads
    // only V5 bone tables. V6 stores relative table offsets and a separate index
    // array (Penumbra.GameData Files/ModelStructs/BoneTableStruct.ReadV6).
    private static MdlFile Read(byte[] bytes)
    {
        using var reader = new LuminaBinaryReader(bytes);
        var file = new MdlFile { FileHeader = ModelFileHeader.Read(reader) };
        if (file.FileHeader.Version is not (0x01000005 or 0x01000006))
            throw new InvalidDataException("Unsupported actor model version.");
        file.VertexDeclarations = new VertexDeclarationStruct[file.FileHeader.VertexDeclarationCount];
        for (int i = 0; i < file.VertexDeclarations.Length; i++) file.VertexDeclarations[i] = VertexDeclarationStruct.Read(reader);
        file.StringCount = reader.ReadUInt16();
        reader.ReadUInt16();
        file.Strings = reader.ReadBytes(checked((int)reader.ReadUInt32()));
        int headerOffset = checked((int)reader.BaseStream.Position);
        file.ModelHeader = reader.ReadStructure<ModelHeader>();
        for (int i = 0; i < file.ModelHeader.ElementIdCount; i++) ElementIdStruct.Read(reader);
        file.Lods = reader.ReadStructuresAsArray<LodStruct>(3);
        file.ExtraLods = file.ModelHeader.ExtraLodEnabled ? reader.ReadStructuresAsArray<ExtraLodStruct>(3) : [];
        file.Meshes = new MeshStruct[file.ModelHeader.MeshCount];
        for (int i = 0; i < file.Meshes.Length; i++) file.Meshes[i] = MeshStruct.Read(reader);
        file.AttributeNameOffsets = reader.ReadUInt32Array(file.ModelHeader.AttributeCount);
        reader.ReadStructuresAsArray<TerrainShadowMeshStruct>(file.ModelHeader.TerrainShadowMeshCount);
        file.Submeshes = reader.ReadStructuresAsArray<SubmeshStruct>(file.ModelHeader.SubmeshCount);
        reader.ReadStructuresAsArray<TerrainShadowSubmeshStruct>(file.ModelHeader.TerrainShadowSubmeshCount);
        reader.ReadUInt32Array(file.ModelHeader.MaterialCount);
        file.BoneNameOffsets = reader.ReadUInt32Array(file.ModelHeader.BoneCount);
        file.BoneTables = new BoneTableStruct[file.ModelHeader.BoneTableCount];
        for (int i = 0; i < file.BoneTables.Length; i++)
        {
            if (file.FileHeader.Version == 0x01000005) file.BoneTables[i] = BoneTableStruct.Read(reader);
            else
            {
                long start = reader.BaseStream.Position;
                ushort offset = reader.ReadUInt16(), count = reader.ReadUInt16();
                reader.BaseStream.Position = start + offset * 4;
                file.BoneTables[i] = new() { BoneIndex = reader.ReadUInt16Array(count) };
                reader.BaseStream.Position = start + 4;
            }
        }
        // V6 repurposes ModelHeader bytes 44..45 as BoneTableArrayCountTotal.
        if (file.FileHeader.Version == 0x01000006)
            reader.BaseStream.Position += BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(headerOffset + 44)) * 2;
        file.Shapes = new ShapeStruct[file.ModelHeader.ShapeCount];
        for (int i = 0; i < file.Shapes.Length; i++) file.Shapes[i] = ShapeStruct.Read(reader);
        file.ShapeMeshes = reader.ReadStructuresAsArray<ShapeMeshStruct>(file.ModelHeader.ShapeMeshCount);
        file.ShapeValues = reader.ReadStructuresAsArray<ShapeValueStruct>(file.ModelHeader.ShapeValueCount);
        return file;
    }
}
