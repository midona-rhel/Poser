using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Poser.Files;

/// <summary>
/// The pose clipboard's wire format, Brio-compatible both ways.
///
/// <para>Brio does NOT put plain pose JSON on the clipboard. Its copy is
/// <c>Clipboard.ToCompressedBase64(pose, version: 1)</c>
/// (Brio Services/Input/Clipboard.cs:29-51, called from
/// FileUIHelpers.cs:784-791) — gzip over a single version byte followed by
/// UTF-8 JSON, the whole compressed stream base64'd, with NO text prefix. Its
/// paste is the inverse (FileUIHelpers.cs:574-595) and accepts nothing else,
/// so that is what <see cref="Encode"/> emits: a Poser copy pastes into Brio
/// and a Brio copy pastes into Poser.</para>
///
/// <para>The clipboard JSON is NOT the .pose file JSON. Brio's clipboard
/// serializer carries only <c>IncludeFields = true</c> — no numerics
/// converters — so Vector3/Quaternion land as objects
/// (<c>{"X":0,"Y":1,"Z":0}</c>) where a .pose file writes the Anamnesis
/// comma-string (<c>"0, 1, 0"</c>). <see cref="Decode"/> therefore tries both
/// shapes, compressed or bare, which also makes pasting the raw text of a
/// .pose file work.</para>
/// </summary>
public static class PoseClipboard
{
    /// <summary>Brio's <c>_clipboardOptions</c> verbatim (Clipboard.cs:25):
    /// fields included, no converters, default naming.</summary>
    private static readonly JsonSerializerOptions BrioOptions = new()
    {
        IncludeFields = true,
    };

    /// <summary>The version byte Brio writes and Poser matches
    /// (FileUIHelpers.cs:789 passes <c>version: 1</c>).</summary>
    private const byte Version = 1;

    /// <summary>
    /// Brio's runtime PoseFile as it serializes on the clipboard: the
    /// JsonDocumentBase metadata (Author, Description, Version, Base64Image,
    /// Tags — omitted, see <see cref="Encode"/>), PoseData's collections and
    /// legacy numerics, and PoseFile's own header fields with Brio's exact
    /// defaults (PoseFile.cs:138-148).
    ///
    /// <para>An independent DTO rather than Poser's <see cref="PoseFile"/>:
    /// the .pose model has no place for a GameVersion or a FaceID, and
    /// putting them there would change what Poser WRITES to disk. This shape
    /// exists only for the wire.</para>
    /// </summary>
    private sealed class BrioPosePayload
    {
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Base64Image { get; set; }

        public PoseFile.BoneData ModelDifference { get; set; } = PoseFile.BoneData.Identity;
        public PoseFile.BoneData ModelAbsoluteValues { get; set; } = PoseFile.BoneData.Identity;

        public Dictionary<string, PoseFile.BoneData> Bones { get; set; } = new();
        public Dictionary<string, PoseFile.BoneData> MainHand { get; set; } = new();
        public Dictionary<string, PoseFile.BoneData> OffHand { get; set; } = new();
        public Dictionary<string, PoseFile.BoneData> Prop { get; set; } = new();
        public Dictionary<string, PoseFile.BoneData> Ornament { get; set; } = new();

        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        /// <summary>Brio's get-only <c>FileExtension</c> — it serializes, so
        /// it is here (PoseFile.cs:138).</summary>
        public string FileExtension => ".pose";

        /// <summary>Brio's current document version (PoseFile.cs:140).</summary>
        public int FileVersion { get; set; } = 3;

        public string TypeName { get; set; } = "Brio Pose";

        /// <summary>Poser delegates appearance to Glamourer and never records
        /// a model id, so Brio's own default rides: 0 means "no appearance
        /// hint", which is exactly what its smart import tests for
        /// (FileUIHelpers.cs:342).</summary>
        public int ModelId { get; set; } = 0;

        public string? RaceSexId { get; set; } = null;
        public int? FaceID { get; set; } = null;

        /// <summary>Brio stamps the live game version here
        /// (<c>Framework.Instance()-&gt;GameVersionString</c>, PoseFile.cs:148).
        /// Poser's pose model carries no such value and the field is
        /// informational on both sides — nothing reads it back — so the
        /// payload states it empty rather than inventing one.</summary>
        public string GameVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// The clipboard payload for a pose, or null when it cannot be built.
    ///
    /// <para>The payload is shaped as BRIO'S runtime PoseFile, not as Poser's
    /// reduced one: Brio's document carries <c>TypeName</c>,
    /// <c>FileExtension</c>, <c>FileVersion</c>, <c>GameVersion</c>,
    /// <c>ModelId</c>, <c>RaceSexId</c> and <c>FaceID</c> on top of the
    /// JsonDocumentBase metadata (PoseFile.cs:136-157, JsonDocumentBase.cs:
    /// 18-33), and its own defaults are reproduced here for the fields Poser
    /// has no value for.</para>
    ///
    /// <para>Tags are deliberately dropped: Brio's clipboard reader has no
    /// TagCollection converter, so a tag array of plain strings — which is
    /// what a Poser file carries — would make Brio reject the whole
    /// paste.</para>
    /// </summary>
    public static string? Encode(PoseFile pose)
    {
        if (pose == null)
            return null;
        try
        {
            var payload = new BrioPosePayload
            {
                TypeName = pose.TypeName,
                Author = pose.Author,
                Description = pose.Description,
                Version = pose.Version,
                Base64Image = pose.Base64Image,
                ModelDifference = pose.ModelDifference,
                ModelAbsoluteValues = pose.ModelAbsoluteValues,
                Bones = pose.Bones,
                MainHand = pose.MainHand,
                OffHand = pose.OffHand,
                Prop = pose.Prop,
                Ornament = pose.Ornament,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Scale = pose.Scale,
            };
            var json = JsonSerializer.Serialize(payload, BrioOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            using var compressed = new MemoryStream();
            using (var zip = new GZipStream(compressed, CompressionMode.Compress))
            {
                zip.WriteByte(Version);
                zip.Write(bytes, 0, bytes.Length);
            }
            return Convert.ToBase64String(compressed.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The pose a clipboard payload carries, or null with a reason fit to show
    /// in a status line. Accepts Brio's compressed base64 and bare JSON in
    /// either numerics shape, and either TAG shape — Brio's TagCollection
    /// writes tag objects where Poser writes strings, and a mismatch there
    /// used to reject the whole payload
    /// (<see cref="Converters.TagListConverter"/>). Never throws.
    ///
    /// <para>Bare JSON is a documented POSER EXTENSION: Brio's paste accepts
    /// nothing but the compressed form, so pasting the raw text of a .pose
    /// file works here and not there. It costs nothing and no Brio-facing
    /// behaviour depends on rejecting it.</para>
    /// </summary>
    public static PoseFile? Decode(string? text, out string? error)
    {
        error = null;
        var payload = text?.Trim();
        if (string.IsNullOrEmpty(payload))
        {
            error = "The clipboard is empty.";
            return null;
        }

        var pose = payload[0] == '{'
            ? FromJson(payload)
            : FromJson(Decompress(payload));
        if (pose == null)
        {
            error = "The clipboard does not hold a pose.";
            return null;
        }
        if (pose.Bones.Count == 0 && pose.MainHand.Count == 0 &&
            pose.OffHand.Count == 0 && pose.Prop.Count == 0 &&
            pose.Ornament.Count == 0)
        {
            // Brio's own emptiness gate (PosingCapability.cs:179-184): a
            // structurally valid document with no bones is not a pose.
            error = "The clipboard pose has no bones.";
            return null;
        }
        return pose;
    }

    /// <summary>The JSON inside a compressed payload, minus the version byte,
    /// or null when the text is not one.</summary>
    private static string? Decompress(string payload)
    {
        try
        {
            var bytes = Convert.FromBase64String(payload);
            using var compressed = new MemoryStream(bytes);
            using var zip = new GZipStream(compressed, CompressionMode.Decompress);
            using var result = new MemoryStream();
            zip.CopyTo(result);
            bytes = result.ToArray();
            if (bytes.Length < 2)
                return null;
            // The leading byte is the format version, not JSON. Brio ignores
            // the value on paste (FileUIHelpers.cs:577 discards the return),
            // so a future version still parses as far as its shape allows.
            return Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Both numerics shapes: the .pose file's comma-strings first
    /// (<see cref="PoseFile.FromJson"/> carries those converters), then Brio's
    /// clipboard objects.</summary>
    private static PoseFile? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        if (PoseFile.FromJson(json) is { } file)
            return file;
        try
        {
            return JsonSerializer.Deserialize<PoseFile>(json, BrioOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
