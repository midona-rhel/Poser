using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Poser.Domain.Transforms;
using Poser.Documents.Files.Converters;

namespace Poser.Documents.Files;

/// <summary>
/// A .pose file. It has the same JSON layout as Brio's .pose files, so a pose
/// saved by either tool loads in the other.
/// </summary>
[Serializable]
public class PoseFile
{
    public string TypeName { get; set; } = "Brio Pose";

    /// <summary>The file format version. Poser writes Brio's current version
    /// (Brio Files/PoseFile.cs:140) so its files look like Brio's own, and
    /// never reads it. A file written elsewhere keeps the version it had.
    /// </summary>
    public int FileVersion { get; set; } = CurrentFileVersion;

    /// <summary>The game version the pose was saved on, as Brio writes it
    /// (<c>Framework.Instance()-&gt;GameVersionString</c>, Brio
    /// PoseFile.cs:148). Only a record: neither Poser nor Brio reads it when
    /// importing. Empty when the game version could not be read.</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>Brio's <c>CurrentVersion</c> for a .pose document.</summary>
    public const int CurrentFileVersion = 3;

    // Metadata (optional)
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public string? Base64Image { get; set; }

    /// <summary>
    /// Poser writes a plain string array, which is what current Brio writes
    /// too — its <c>TagCollection</c> serializes through the globally
    /// registered <c>TagCollectionConverter</c>. Older documents carry an
    /// array of tag objects; the converter reads both, because a shape
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
    /// Where the pose was saved, when the writer recorded it, like a scene
    /// file. The name is saved next to the id because the library lists files
    /// without the game data that would turn an id into a name (see
    /// <c>docs/features/scenes.md</c>).
    ///
    /// <para>Both are left out of the file when not set, so such a file has
    /// only the members Brio knows. A file without them is listed by its date
    /// alone.</para>
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
    /// False for a pose read from an old <c>.cmp</c> file. Those files store
    /// only rotations and scales, so an import of one never writes positions.
    /// Not saved in the file.
    /// </summary>
    [JsonIgnore]
    public bool HasPositions { get; set; } = true;

    /// <summary>
    /// The top-level members of the file that this class has no property for,
    /// kept as they were read. Without them, editing a file's author or tags
    /// would drop whatever Brio or a newer Poser wrote there. Brio keeps them
    /// for the same reason (Brio Services/Library/Sources/FileSource.cs:341).
    ///
    /// <para>Nothing in Poser reads or changes these members; they are written
    /// back after the named ones. They come only from a file that passed the
    /// size and depth limits in <see cref="PoseFileLimits"/>.</para>
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedMembers { get; set; }

    /// <summary>One bone's position, rotation and scale, as Brio writes them.</summary>
    [Serializable]
    public class BoneData
    {
        public static implicit operator Transform(BoneData bone) =>
            new(bone.Position, bone.Rotation, bone.Scale);

        public static implicit operator BoneData(Transform transform) => new()
        { Position = transform.Position, Rotation = transform.Rotation, Scale = transform.Scale };

        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public static BoneData Identity => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };
    }

    // Accepts and emits Brio-compatible wire conventions: invariant comma-space
    // numeric strings, PascalCase, pretty printing, relaxed escaping, tolerated
    // trailing commas/unknown members, and both supported tag shapes.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = PoserJsonContracts.TypeInfo,
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
    /// <see cref="JsonOptions"/> for reading a file's details when it is
    /// selected in the library. The picture in <see cref="Base64Image"/> is
    /// read as "*" or an empty string, which says whether there is one without
    /// keeping it in memory.
    /// </summary>
    internal static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonOptions)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(PoseFile))
                        return;
                    foreach (var property in info.Properties)
                        if (property.Name == nameof(Base64Image))
                            property.CustomConverter = new ThumbnailPresenceConverter();
                },
            },
        },
    };

    private sealed class ThumbnailPresenceConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException($"{nameof(Base64Image)} must be a string.");
            var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            return length == 0 ? string.Empty : "*";
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>
    /// Reads a pose file a person picked to import: a <c>.pose</c> file, or an
    /// old CMTool <c>.cmp</c> file converted to the same shape. Every pose import
    /// from a file reads it here once, and the caller passes the result on, so
    /// a broken file is reported with the reason this read gives.
    /// Old Anamnesis bone names are changed to the game's names.
    /// </summary>
    public static PoseFileReadOutcome Read(string path)
    {
        if (!path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase))
        {
            var read = AtomicPoseFileStore.Default.Read(path);
            read.Pose?.SanitizeBoneNames();
            return read;
        }
        try
        {
            var pose = CMToolPoseFile.Load(path).Upgrade();
            pose.HasPositions = false;
            return PoseFileReadOutcome.Success(pose);
        }
        catch (Exception ex)
        {
            return PoseFileReadOutcome.Failed(PoseFileStoreFailure.Create(
                PoseFileStoreFailureKind.Read,
                $"Reading the .cmp pose file failed: {ex.Message}",
                path));
        }
    }

    /// <summary>
    /// Reads a pose from JSON text, or returns null when the text is not a
    /// valid pose. Used for a pose pasted from the clipboard and for the
    /// built-in A-pose and T-pose; <see cref="AtomicPoseFileStore.Parse"/>
    /// also says why text was refused.
    /// </summary>
    public static PoseFile? FromJson(string json) =>
        AtomicPoseFileStore.Default.Parse(json).Pose;

    /// <summary>
    /// Converts Anamnesis bone names to game bone names.
    /// Required for compatibility with old Anamnesis pose files.
    /// </summary>
    public void SanitizeBoneNames()
    {
        var newBones = new Dictionary<string, BoneData>();
        // Brio preserves document order: the last legacy/canonical alias wins.
        foreach (var bone in Bones)
        {
            newBones[AnamnesisBoneNameConverter.ToGame(bone.Key)] = bone.Value;
        }
        Bones = newBones;
    }
}
