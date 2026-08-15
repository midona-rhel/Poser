using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// The pose clipboard's wire format. The point of these is CROSS-TOOL: Brio's
/// clipboard is not plain pose JSON but gzip over a version byte plus JSON,
/// base64'd (Brio Services/Input/Clipboard.cs:29-76), serialized with nothing
/// but <c>IncludeFields = true</c> — so its numerics are objects where a .pose
/// file writes Anamnesis comma-strings.
///
/// <para>The "Brio side" of every test is an INDEPENDENT model
/// (<see cref="BrioPoseFile"/>) transcribed from Brio's own runtime types
/// (Files/PoseFile.cs:80-157, Files/JsonDocumentBase.cs:18-33,
/// Services/Library/Tags/TagCollection.cs) — not Poser's reduced PoseFile.
/// Decoding through Poser's own model on both sides would have proved only
/// that Poser agrees with itself, which is exactly the hole this replaces: a
/// field Brio carries and Poser does not could go missing without a single
/// failure.</para>
/// </summary>
public class PoseClipboardTests
{
    /// <summary>Brio's <c>_clipboardOptions</c> (Clipboard.cs:25).</summary>
    private static readonly JsonSerializerOptions BrioOptions = new()
    {
        IncludeFields = true,
    };

    // ── Brio's runtime document, transcribed ────────────────────────────

    private sealed class BrioBone
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }
    }

    /// <summary>Brio's <c>Tag</c> as it serializes (Tag.cs:17-22): a private
    /// backing field behind four public members.</summary>
    private sealed class BrioTag
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
        public bool IsToolGenerated { get; set; }
    }

    /// <summary>
    /// <c>PoseFile : PoseData : JsonDocumentBase</c>, every serialized member
    /// in one place with Brio's own defaults. <c>Tags</c> is a
    /// <c>TagCollection : ICollection&lt;Tag&gt;</c>, so it lands on the wire
    /// as an array of tag OBJECTS.
    /// </summary>
    private sealed class BrioPoseFile
    {
        // JsonDocumentBase
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Base64Image { get; set; }
        public List<BrioTag>? Tags { get; set; }

        // PoseData
        public BrioBone ModelDifference { get; set; } = new();
        public BrioBone ModelAbsoluteValues { get; set; } = new();
        public Dictionary<string, BrioBone> Bones { get; set; } = new();
        public Dictionary<string, BrioBone> MainHand { get; set; } = new();
        public Dictionary<string, BrioBone> OffHand { get; set; } = new();
        public Dictionary<string, BrioBone> Prop { get; set; } = new();
        public Dictionary<string, BrioBone> Ornament { get; set; } = new();
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        // PoseFile
        public string FileExtension { get; set; } = ".pose";
        public int FileVersion { get; set; } = 3;
        public string TypeName { get; set; } = "Brio Pose";
        public int ModelId { get; set; }
        public string? RaceSexId { get; set; }
        public int? FaceID { get; set; }
        public string GameVersion { get; set; } = "2026.06.18.0000.0000";
    }

    private static readonly Vector3 HeadPosition = new(0.1f, 1.5f, -0.25f);
    private static readonly Quaternion HeadRotation = new(0f, 0.25f, 0f, 0.9682458f);
    private static readonly Vector3 LegPosition = new(-1f, 0f, 2f);

    private static PoseFile SamplePose()
    {
        var pose = new PoseFile();
        pose.Bones["j_kao"] = new PoseFile.BoneData
        {
            Position = HeadPosition,
            Rotation = HeadRotation,
            Scale = Vector3.One,
        };
        pose.Bones["j_asi_a_l"] = new PoseFile.BoneData
        {
            Position = LegPosition,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        return pose;
    }

    private static BrioPoseFile BrioSamplePose()
    {
        var pose = new BrioPoseFile();
        pose.Bones["j_kao"] = new BrioBone
        {
            Position = HeadPosition,
            Rotation = HeadRotation,
            Scale = Vector3.One,
        };
        pose.Bones["j_asi_a_l"] = new BrioBone
        {
            Position = LegPosition,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        return pose;
    }

    /// <summary>Brio's ToCompressedBase64, replicated.</summary>
    private static string BrioEncode<T>(T data, byte version = 1)
    {
        var json = JsonSerializer.Serialize(data, BrioOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var compressed = new MemoryStream();
        using(var zip = new GZipStream(compressed, CompressionMode.Compress))
        {
            zip.WriteByte(version);
            zip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(compressed.ToArray());
    }

    /// <summary>Brio's FromCompressedBase64 INTO ITS OWN MODEL — what has to
    /// succeed for a Poser copy to paste into Brio.</summary>
    private static BrioPoseFile? BrioDecode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var compressed = new MemoryStream(bytes);
        using var zip = new GZipStream(compressed, CompressionMode.Decompress);
        using var result = new MemoryStream();
        zip.CopyTo(result);
        bytes = result.ToArray();
        Assert.Equal(1, bytes[0]);
        var json = Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1);
        return JsonSerializer.Deserialize<BrioPoseFile>(json, BrioOptions);
    }

    [Fact]
    public void RoundTripsItsOwnPayload()
    {
        var payload = PoseClipboard.Encode(SamplePose());
        Assert.NotNull(payload);

        var pose = PoseClipboard.Decode(payload, out var error);

        Assert.Null(error);
        Assert.NotNull(pose);
        Assert.Equal(2, pose!.Bones.Count);
        Assert.Equal(HeadPosition, pose.Bones["j_kao"].Position);
        Assert.Equal(HeadRotation, pose.Bones["j_kao"].Rotation);
    }

    /// <summary>A pose copied in Brio pastes in Poser.</summary>
    [Fact]
    public void DecodesBriosOwnClipboardPayload()
    {
        var payload = BrioEncode(BrioSamplePose());

        var pose = PoseClipboard.Decode(payload, out var error);

        Assert.Null(error);
        Assert.NotNull(pose);
        Assert.Equal(LegPosition, pose!.Bones["j_asi_a_l"].Position);
    }

    /// <summary>
    /// The tag shape, which used to reject the WHOLE payload: Brio's
    /// TagCollection writes tag objects where Poser writes strings. A Brio
    /// copy of a tagged pose has to paste, tags and all.
    /// </summary>
    [Fact]
    public void DecodesBriosTagObjects()
    {
        var tagged = BrioSamplePose();
        tagged.Tags = new List<BrioTag>
        {
            new() { Name = "expression", DisplayName = "expression" },
            new() { Name = "dawntrail", DisplayName = "dawntrail", IsToolGenerated = true },
        };

        var pose = PoseClipboard.Decode(BrioEncode(tagged), out var error);

        Assert.Null(error);
        Assert.NotNull(pose);
        Assert.Equal(new[] { "expression", "dawntrail" }, pose!.Tags);
        Assert.Equal(2, pose.Bones.Count);
    }

    /// <summary>The same object shape in a .pose FILE — Brio writes its tags
    /// that way to disk too, and the loader must not reject the document over
    /// them.</summary>
    [Fact]
    public void LoadsAPoseFileCarryingBriosTagObjects()
    {
        const string json = """
        {
          "TypeName": "Brio Pose",
          "Tags": [ { "DisplayName": "sitting", "Name": "sitting", "Aliases": [], "IsToolGenerated": false } ],
          "Bones": { "j_kao": { "Position": "0, 0, 0", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1" } }
        }
        """;

        var pose = PoseFile.FromJson(json);

        Assert.NotNull(pose);
        Assert.Equal(new[] { "sitting" }, pose!.Tags);
        Assert.Single(pose.Bones);
    }

    /// <summary>A pose copied in Poser pastes in Brio: the version byte is
    /// there, the gzip unwraps, and the JSON deserializes into BRIO'S model —
    /// header fields included.</summary>
    [Fact]
    public void EmitsWhatBrioCanPaste()
    {
        var payload = PoseClipboard.Encode(SamplePose());
        Assert.NotNull(payload);

        var pose = BrioDecode(payload!);

        Assert.NotNull(pose);
        Assert.Equal(2, pose!.Bones.Count);
        Assert.Equal(HeadPosition, pose.Bones["j_kao"].Position);
    }

    /// <summary>Every header field Brio's document carries is on the wire with
    /// its own default — the payload is shaped as Brio's PoseFile, not as
    /// Poser's reduced one.</summary>
    [Fact]
    public void CarriesBriosDocumentHeaderFields()
    {
        var pose = BrioDecode(PoseClipboard.Encode(SamplePose())!);

        Assert.NotNull(pose);
        Assert.Equal("Brio Pose", pose!.TypeName);
        Assert.Equal(".pose", pose.FileExtension);
        Assert.Equal(3, pose.FileVersion);
        Assert.Equal(0, pose.ModelId);
        Assert.Null(pose.RaceSexId);
        Assert.Null(pose.FaceID);
        // Poser records no game version; the field is present and empty
        // rather than absent or invented.
        Assert.Equal(string.Empty, pose.GameVersion);
    }

    /// <summary>Tags never ride the clipboard OUT: Brio's clipboard reader has
    /// no TagCollection converter, so a plain string array there would make it
    /// reject the entire paste.</summary>
    [Fact]
    public void DropsTagsFromThePayload()
    {
        var tagged = SamplePose();
        tagged.Tags = new List<string> { "expression" };

        var pose = BrioDecode(PoseClipboard.Encode(tagged)!);

        Assert.NotNull(pose);
        Assert.Null(pose!.Tags);
    }

    /// <summary>
    /// DELIBERATE POSER EXTENSION, pinned: the raw text of a .pose file pastes
    /// here. Brio's paste accepts the compressed form and nothing else
    /// (FileUIHelpers.cs:574-584), so this is Poser accepting MORE, never
    /// something Brio would reject — it costs nothing on the interop seam.
    /// </summary>
    [Fact]
    public void DecodesPlainPoseFileJson()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"poser-clipboard-{Guid.NewGuid():N}.pose");
        try
        {
            Assert.True(SamplePose().Save(path));

            var pose = PoseClipboard.Decode(File.ReadAllText(path), out var error);

            Assert.Null(error);
            Assert.NotNull(pose);
            Assert.Equal(2, pose!.Bones.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyClipboardSaysSo(string? text)
    {
        Assert.Null(PoseClipboard.Decode(text, out var error));
        Assert.Equal("The clipboard is empty.", error);
    }

    [Theory]
    [InlineData("just some copied chat text")]
    [InlineData("{ not json at all")]
    [InlineData("bm90IGEgcG9zZQ==")]
    public void UnreadableClipboardFailsWithAReason(string text)
    {
        Assert.Null(PoseClipboard.Decode(text, out var error));
        Assert.Equal("The clipboard does not hold a pose.", error);
    }

    /// <summary>Brio's own emptiness gate (PosingCapability.cs:179-184): a
    /// well-formed document with no bones is not a pose.</summary>
    [Fact]
    public void BoneLessPoseFailsWithAReason()
    {
        var payload = PoseClipboard.Encode(new PoseFile());

        Assert.Null(PoseClipboard.Decode(payload, out var error));
        Assert.Equal("The clipboard pose has no bones.", error);
    }
}
