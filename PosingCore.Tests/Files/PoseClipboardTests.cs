using System;
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
/// file writes Anamnesis comma-strings. Both halves of that are replicated
/// here rather than referenced, so a change on either side of the seam shows
/// up as a failure instead of a silent incompatibility.
/// </summary>
public class PoseClipboardTests
{
    /// <summary>Brio's <c>_clipboardOptions</c> (Clipboard.cs:25).</summary>
    private static readonly JsonSerializerOptions BrioOptions = new()
    {
        IncludeFields = true,
    };

    private static PoseFile SamplePose()
    {
        var pose = new PoseFile();
        pose.Bones["j_kao"] = new PoseFile.BoneData
        {
            Position = new Vector3(0.1f, 1.5f, -0.25f),
            Rotation = new Quaternion(0f, 0.25f, 0f, 0.9682458f),
            Scale = Vector3.One,
        };
        pose.Bones["j_asi_a_l"] = new PoseFile.BoneData
        {
            Position = new Vector3(-1f, 0f, 2f),
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

    /// <summary>Brio's FromCompressedBase64, replicated — what has to succeed
    /// for a Poser copy to paste into Brio.</summary>
    private static PoseFile? BrioDecode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var compressed = new MemoryStream(bytes);
        using var zip = new GZipStream(compressed, CompressionMode.Decompress);
        using var result = new MemoryStream();
        zip.CopyTo(result);
        bytes = result.ToArray();
        Assert.Equal(1, bytes[0]);
        var json = Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1);
        return JsonSerializer.Deserialize<PoseFile>(json, BrioOptions);
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
        Assert.Equal(
            new Vector3(0.1f, 1.5f, -0.25f), pose.Bones["j_kao"].Position);
        Assert.Equal(
            new Quaternion(0f, 0.25f, 0f, 0.9682458f),
            pose.Bones["j_kao"].Rotation);
    }

    /// <summary>A pose copied in Brio pastes in Poser.</summary>
    [Fact]
    public void DecodesBriosOwnClipboardPayload()
    {
        var payload = BrioEncode(SamplePose());

        var pose = PoseClipboard.Decode(payload, out var error);

        Assert.Null(error);
        Assert.NotNull(pose);
        Assert.Equal(
            new Vector3(-1f, 0f, 2f), pose!.Bones["j_asi_a_l"].Position);
    }

    /// <summary>A pose copied in Poser pastes in Brio: the version byte is
    /// there, the gzip unwraps, and the JSON deserializes under Brio's own
    /// serializer options.</summary>
    [Fact]
    public void EmitsWhatBrioCanPaste()
    {
        var payload = PoseClipboard.Encode(SamplePose());
        Assert.NotNull(payload);

        var pose = BrioDecode(payload!);

        Assert.NotNull(pose);
        Assert.Equal(2, pose!.Bones.Count);
        Assert.Equal(new Vector3(0.1f, 1.5f, -0.25f), pose.Bones["j_kao"].Position);
    }

    /// <summary>Tags never ride the clipboard: Brio's clipboard reader has no
    /// TagCollection converter, so a plain string array there would make it
    /// reject the entire paste.</summary>
    [Fact]
    public void DropsTagsFromThePayload()
    {
        var tagged = SamplePose();
        tagged.Tags = new System.Collections.Generic.List<string> { "expression" };

        var pose = BrioDecode(PoseClipboard.Encode(tagged)!);

        Assert.NotNull(pose);
        Assert.Null(pose!.Tags);
    }

    /// <summary>The other shape a user can paste: the raw text of a .pose
    /// file, whose numerics are Anamnesis comma-strings.</summary>
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
