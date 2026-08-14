using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poser.Files;

/// <summary>
/// Brio-compatible pose file format (.pose).
/// Matches Brio's JSON structure exactly for full compatibility.
/// </summary>
[Serializable]
public class PoseFile
{
    public string TypeName { get; set; } = "Brio Pose";

    // Metadata (optional)
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public string? Base64Image { get; set; }

    /// <summary>
    /// Poser writes a plain string array, which is what current Brio writes
    /// too — its <c>TagCollection</c> serializes through the globally
    /// registered <c>TagCollectionConverter</c>. Older documents carry an
    /// array of tag OBJECTS; the converter reads both, because a shape
    /// mismatch rejects the entire document, not just its tags
    /// (<see cref="Converters.TagListConverter"/>).
    /// </summary>
    [JsonConverter(typeof(Converters.TagListConverter))]
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Brio Smart Import metadata hint, Brio's wire names exactly (Brio
    /// Files/PoseFile.cs:143-145). Brio writes them at save
    /// (MetadataModal.cs:199-202) and Smart Import consumes ModelId
    /// (FileUIHelpers.ResolveSmartImport:341-351): a non-zero ModelId on a
    /// human target redraws the target as that creature before posing.
    /// Poser populates ModelId on export from the actor's current model id;
    /// RaceSexId/FaceID derive from customize data Poser does not own, so
    /// they are declared only to round-trip Brio-authored files unharmed.
    /// </summary>
    public int ModelId { get; set; }
    public string? RaceSexId { get; set; }
    public int? FaceID { get; set; }

    /// <summary>
    /// Where the capture ran, when the writer recorded one — the same pair a
    /// scene document carries, and for the same reason: the NAME is persisted
    /// beside the id because the listings that read it run with no game data
    /// to resolve an id with (see <c>docs/features/scenes.md</c>).
    ///
    /// <para>Both are OMITTED when unset, so a document with no place is
    /// byte-identical to one written before these members existed and Poser's
    /// ordinary exports keep exactly the Brio-compatible member set they had.
    /// ABSENT is the only way a file says "no place was recorded"; a listing
    /// then groups it by its day alone and infers nothing.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint TerritoryId { get; set; }

    /// <inheritdoc cref="TerritoryId"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PlaceName { get; set; }

    public BoneData ModelDifference { get; set; } = BoneData.Identity;
    public BoneData ModelAbsoluteValues { get; set; } = BoneData.Identity;

    public Dictionary<string, BoneData> Bones { get; set; } = new();
    public Dictionary<string, BoneData> MainHand { get; set; } = new();
    public Dictionary<string, BoneData> OffHand { get; set; } = new();
    public Dictionary<string, BoneData> Prop { get; set; } = new();
    public Dictionary<string, BoneData> Ornament { get; set; } = new();

    // Legacy fields for compatibility with other pose tools
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    /// <summary>
    /// Every root member this model does not name, carried verbatim.
    /// System.Text.Json SKIPS unmapped members by default, so without this any
    /// read-modify-write of a document Poser did not author would silently
    /// drop what Brio (or a newer Poser) writes and consumes at that root —
    /// <c>FileVersion</c>, <c>GameVersion</c>, whatever the format gains next.
    /// Brio's own metadata edit is careful for the same reason: it edits
    /// through the full-fidelity document
    /// (Brio Services/Library/Sources/FileSource.cs:341).
    ///
    /// <para>This is a preservation seam, not a data model: nothing in Poser
    /// reads or writes into it, so it can never shadow a named member, and a
    /// member that later gains a property here simply stops arriving. Carried
    /// members are re-emitted after the named ones. It adds no unbounded
    /// surface — only a read that already passed the codec's file-size and
    /// JSON-depth bounds (<see cref="PoseFileLimits"/>) can populate it, and
    /// the write path re-decodes and re-bounds the serialized bytes before
    /// any file is replaced.</para>
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedMembers { get; set; }

    /// <summary>
    /// Bone transform data matching Brio's format.
    /// </summary>
    [Serializable]
    public class BoneData
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public static BoneData Identity => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        public static implicit operator Transform(BoneData bone)
        {
            return new Transform(bone.Position, bone.Rotation, bone.Scale);
        }

        public static implicit operator BoneData(Transform transform)
        {
            return new BoneData
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale
            };
        }
    }

    // Accepts and emits Brio-compatible wire conventions: invariant comma-space
    // numeric strings, PascalCase, pretty printing, relaxed escaping, tolerated
    // trailing commas/unknown members, and both supported tag shapes.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        MaxDepth = PoseFileLimits.MaxJsonDepth,
        PropertyNamingPolicy = null, // Keep PascalCase to match Brio
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
            new Converters.Vector2Converter(),
            new Converters.Vector3Converter(),
            new Converters.Vector4Converter(),
            new Converters.QuaternionConverter()
        }
    };

    /// <summary>
    /// Lossy compatibility load. Returns null for every typed read, size,
    /// JSON, or validation failure; new storage workflows use
    /// <see cref="AtomicPoseFileStore.Read"/>.
    /// </summary>
    public static PoseFile? Load(string path)
    {
        // Intentionally lossy compatibility wrapper. New storage callers use
        // AtomicPoseFileStore.Read so the typed failure and path survive.
        return AtomicPoseFileStore.Default.Read(path).Pose;
    }

    /// <summary>
    /// Lossy compatibility parse. Returns null for every typed size, JSON, or
    /// validation failure; new storage workflows use
    /// <see cref="AtomicPoseFileStore.Parse"/>.
    /// </summary>
    public static PoseFile? FromJson(string json)
    {
        // Intentionally lossy compatibility wrapper for clipboard/rest-pose
        // callers that predate the typed ordinary-pose codec outcome.
        return AtomicPoseFileStore.Default.Parse(json).Pose;
    }

    /// <summary>
    /// Lossy compatibility save. Returns false for every typed validation,
    /// serialization, temp, flush, validation, replace, or move failure; new
    /// storage workflows use <see cref="AtomicPoseFileStore.Write"/>.
    /// </summary>
    public bool Save(string path)
    {
        // Intentionally lossy compatibility wrapper. The typed store retains
        // the phase and any undeletable temp as recovery evidence.
        return AtomicPoseFileStore.Default.Write(this, path).Succeeded;
    }

    /// <summary>
    /// Converts Anamnesis bone names to game bone names.
    /// Required for compatibility with old Anamnesis pose files.
    /// </summary>
    public void SanitizeBoneNames()
    {
        var aliases = PoseFileValidation.ValidateAnamnesisAliases(Bones);
        if (!aliases.Succeeded)
            throw new InvalidDataException(aliases.Failure!.Detail);

        var newBones = new Dictionary<string, BoneData>();
        foreach (var bone in Bones.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            newBones[AnamnesisBoneNameConverter.ToGame(bone.Key)] = bone.Value;
        }
        Bones = newBones;
    }
}
