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
    private static byte[] Pap(params (string Name, short Binding, bool Face)[] clips)
    {
        if (clips.Length == 0) clips = [("cbem_pose01_2lp", 0, false)];
        var timeline = IdlePapBuilder.Timeline("cbem_pose01_2lp", 70);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("pap "u8); w.Write(0x00020001); w.Write((short)clips.Length);
        w.Write((ushort)801); w.Write((byte)0); w.Write((byte)1);
        w.Write(26); w.Write(26 + clips.Length * 40); w.Write(36 + clips.Length * 40);
        foreach (var clip in clips)
        {
            var name = Encoding.ASCII.GetBytes(clip.Name);
            w.Write(name); w.Write(new byte[32 - name.Length]);
            w.Write((short)0); w.Write(clip.Binding); w.Write(clip.Face ? 1 : 0);
        }
        w.Write(new byte[10]); w.Write(timeline);
        return stream.ToArray();
    }

    [Fact]
    public void Multi_clip_templates_select_body_and_face_and_remap_output_binding()
    {
        var body = new PapAnimationDocument(Pap(("cbem_pose03_1", 2, false), ("cbep_e_pose03_1", 0, false)));
        var face = new PapAnimationDocument(Pap(("cfxf_comeon", 3, true), ("cfxf_smile", 1, true)));
        Assert.Equal(70, body.DurationFrames);
        Assert.Throws<InvalidDataException>(() => IdlePapBuilder.BuildClip(face, new byte[8], 70));
        var files = IdlePapBuilder.Files("c1301", "f0002", body, body, face,
            new byte[8], new byte[8], new byte[8], new byte[8]);
        Assert.All(files, file => Assert.Equal(0, Assert.Single(new PapAnimationDocument(file.Bytes).Clips).BindingIndex));
        Assert.Equal("cbem_pose03_1", new PapAnimationDocument(files[0].Bytes).Clips[0].Name);
        Assert.Equal("cfxf_comeon", new PapAnimationDocument(files[2].Bytes).Clips[0].Name);
        Assert.Contains("cfxf_comeon", Encoding.ASCII.GetString(files[0].Bytes));
        Assert.DoesNotContain("cfxf_grin", Encoding.ASCII.GetString(files[0].Bytes));
        Assert.DoesNotContain("cfxf_smile", Encoding.ASCII.GetString(files[2].Bytes));
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
    public void Body_loads_a_separate_facial_library_and_triggers_its_motion()
    {
        var pair = IdlePapBuilder.BuildClip(new(Pap()), new byte[8], 70, "emot/poser_pose01_loop");
        var document = new PapAnimationDocument(pair);
        Assert.Single(document.Clips);
        Assert.Equal("cbem_pose01_2lp", document.Clips[0].Name);
        Assert.False(document.Clips[0].IsFace);
        int start = BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(22));
        int count = BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(start + 8));
        int item = start + 12;
        var paths = new List<string>();
        string? library = null;
        for (int i = 0; i < count; i++)
        {
            var type = Encoding.ASCII.GetString(pair, item, 4);
            if (type == "TMPP")
            {
                int text = item + 8 + BinaryPrimitives.ReadInt32LittleEndian(pair.AsSpan(item + 8));
                library = Encoding.ASCII.GetString(pair, text, Array.IndexOf(pair, (byte)0, text) - text);
            }
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
        Assert.Equal("emot/poser_pose01_loop", library);
    }

    [Fact]
    public void Both_declared_face_libraries_are_packaged_with_face_bindings_and_full_duration()
    {
        var face = Pap();
        face.AsSpan(26, 32).Clear();
        Encoding.ASCII.GetBytes("cfxf_grin").CopyTo(face, 26);
        BinaryPrimitives.WriteInt16LittleEndian(face.AsSpan(58), 17);
        BinaryPrimitives.WriteInt32LittleEndian(face.AsSpan(62), 1);
        var files = IdlePapBuilder.Files("c0801", "f0002", new(Pap()), new(Pap()), new(face),
            new byte[8], new byte[8], new byte[8], new byte[8]);
        Assert.Equal(4, files.Count);
        for (int i = 2; i < 4; i++)
        {
            Assert.StartsWith("chara/human/c0801/animation/f0002/nonresident/emot/poser_pose01_", files[i].GamePath);
            var document = new PapAnimationDocument(files[i].Bytes);
            Assert.True(Assert.Single(document.Clips).IsFace);
            Assert.Equal(0, document.Clips[0].BindingIndex);
            int timeline = BinaryPrimitives.ReadInt32LittleEndian(files[i].Bytes.AsSpan(22));
            Assert.Equal(i == 2 ? 45 : 70, BinaryPrimitives.ReadInt16LittleEndian(files[i].Bytes.AsSpan(timeline + 24)));
        }
        Assert.DoesNotContain(files, f => f.GamePath.Contains("/resident/face.pap") || f.GamePath.EndsWith(".tmb"));
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
    public void Chosen_slot_paths_and_durations_are_used_for_body_and_face_together()
    {
        var face = Pap();
        BinaryPrimitives.WriteInt32LittleEndian(face.AsSpan(62), 1);
        var files = IdlePapBuilder.Files("c0101", "f0002", new(Pap()), new(Pap()), new(face),
            new byte[8], new byte[8], new byte[8], new byte[8], 4, 90, 120);
        Assert.All(files, file => Assert.Contains("pose04_", file.GamePath));
        Assert.All(files, file => Assert.StartsWith("chara/human/c0101/", file.GamePath));
        Assert.Equal(new[] { 90, 120, 90, 120 }, files.Select(f => new PapAnimationDocument(f.Bytes).DurationFrames));
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
        public Task<IdleModChoices> DescribeAsync(ActorId actor) => Task.FromResult(new IdleModChoices("Test", 801, [new(801, "Miqo'te female", [1])]));
        public Task<IdleModPackage> CaptureAsync(ActorId actor, IdleModOptions options) => result;
    }
}
