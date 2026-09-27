using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Poser.Application.Animation;
using Poser.Documents.Animation;
using Poser.Domain.Identity;

namespace Poser.Tests.Files;

public sealed class IdleModPackageTests
{
    private const string GamePath = "chara/human/c0801/animation/a0001/bt_common/emote/pose01_loop.pap";
    private static byte[] Pap()
    {
        var timeline = IdlePapBuilder.Timeline("cbem_pose01_2lp", 70);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("pap "u8); w.Write(0x00020001); w.Write((short)1);
        w.Write((ushort)801); w.Write((byte)0); w.Write((byte)1);
        w.Write(26); w.Write(66); w.Write(76);
        var name = Encoding.ASCII.GetBytes("cbem_pose01_2lp");
        w.Write(name); w.Write(new byte[32 - name.Length]);
        w.Write((short)0); w.Write((short)0); w.Write(0);
        w.Write(new byte[10]); w.Write(timeline);
        return stream.ToArray();
    }

    [Fact]
    public void Replacement_preserves_clip_metadata_and_timeline_bytes()
    {
        var source = Pap();
        var document = new PapAnimationDocument(source);
        var replaced = document.ReplaceHavok(new byte[19]);
        var result = new PapAnimationDocument(replaced);
        Assert.Equal(document.Clips, result.Clips);
        Assert.Equal(document.ModelId, result.ModelId);
        int timeline = BinaryPrimitives.ReadInt32LittleEndian(replaced.AsSpan(22));
        Assert.Equal(0, timeline % 4);
        Assert.Equal(source[76..], replaced[timeline..]);
    }

    [Fact]
    public void Body_and_face_are_distinct_bindings_with_resolvable_timeline_names()
    {
        var pair = IdlePapBuilder.BuildPair(new(Pap()), new byte[8], 70);
        var document = new PapAnimationDocument(pair);
        Assert.Equal("cbem_pose01_2lp", document.Clips[0].Name);
        Assert.False(document.Clips[0].IsFace);
        Assert.True(document.Clips[1].IsFace);
        Assert.Equal(1, document.Clips[1].BindingIndex);
        int start = BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(22));
        int count = BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(start + 8));
        int item = start + 12;
        var paths = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var type = Encoding.ASCII.GetString(pair, item, 4);
            if (type is "C009" or "C010")
            {
                int field = item + (type == "C009" ? 20 : 32);
                int text = item + 8 + BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(field));
                int end = Array.IndexOf(pair, (byte)0, text);
                paths.Add(Encoding.ASCII.GetString(pair, text, end - text));
            }
            item += BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(item + 4));
        }
        Assert.Equal(new[] { "cbem_pose01_2lp", "cfxf_grin" }, paths);
    }

    [Theory]
    [InlineData(14, -1)]
    [InlineData(18, 30)]
    [InlineData(22, int.MaxValue)]
    public void Corrupt_sections_are_rejected(int field, int value)
    {
        var bytes = Pap();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(field), value);
        Assert.Throws<InvalidDataException>(() => new PapAnimationDocument(bytes));
    }

    [Fact]
    public void Pmp_maps_both_files_and_never_overwrites_existing_output()
    {
        var directory = Directory.CreateTempSubdirectory("poser-idle-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "pose.pmp");
            var package = new IdleModPackage("Test pose", "Description", [new(GamePath, Pap()), new(GamePath.Replace("loop", "start"), Pap())]);
            package.WriteNew(path);
            using (var zip = ZipFile.OpenRead(path))
            {
                using var metadata = JsonDocument.Parse(zip.GetEntry("meta.json")!.Open());
                Assert.Equal("Test pose", metadata.RootElement.GetProperty("Name").GetString());
                using var mapping = JsonDocument.Parse(zip.GetEntry("default_mod.json")!.Open());
                string target = mapping.RootElement.GetProperty("Files").GetProperty(GamePath).GetString()!;
                using var data = new MemoryStream();
                zip.GetEntry(target)!.Open().CopyTo(data);
                Assert.Equal(Pap(), data.ToArray());
            }
            var original = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => package.WriteNew(path));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Capture_failure_writes_nothing_and_releases_busy_gate()
    {
        var completion = new TaskCompletionSource<IdleModPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var export = new IdleModExport(new Runtime(completion.Task));
        var directory = Directory.CreateTempSubdirectory("poser-idle-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "pose.pmp");
            var first = export.ExportAsync(default, path);
            Assert.True(export.Busy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => export.ExportAsync(default, path));
            completion.SetException(new InvalidDataException("Actor disappeared"));
            await Assert.ThrowsAsync<InvalidDataException>(() => first);
            Assert.False(export.Busy);
            Assert.Empty(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    private sealed class Runtime(Task<IdleModPackage> result) : IIdleModRuntime
    {
        public Task<IdleModPackage> CaptureAsync(ActorId actor) => result;
    }
}
