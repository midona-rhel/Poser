using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Poser.Application.Animation;
using Poser.Documents.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Files;

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
    public void Body_and_face_libraries_package_with_bindings_slots_and_durations()
    {
        // Multi-clip templates: the body and face clips are selected and remapped to binding 0.
        var body = new PapAnimationDocument(Pap(("cbem_pose03_1", 2, false), ("cbep_e_pose03_1", 0, false)));
        var face = new PapAnimationDocument(Pap(("cfxf_comeon", 3, true), ("cfxf_smile", 1, true)));
        Assert.Throws<InvalidDataException>(() => IdlePapBuilder.BuildClip(face, new byte[8], 70));
        var files = IdlePapBuilder.Files("c1301", "f0002", body, body, face,
            new byte[8], new byte[8], new byte[8], new byte[8]);
        Assert.All(files, file => Assert.Equal(0, Assert.Single(new PapAnimationDocument(file.Bytes).Clips).BindingIndex));
        Assert.Equal("cbem_pose03_1", new PapAnimationDocument(files[0].Bytes).Clips[0].Name);
        Assert.True(new PapAnimationDocument(files[2].Bytes).Clips[0].IsFace);
        Assert.Contains("cfxf_comeon", Encoding.ASCII.GetString(files[0].Bytes));
        Assert.DoesNotContain("cfxf_smile", Encoding.ASCII.GetString(files[2].Bytes));
        Assert.StartsWith("chara/human/c1301/animation/f0002/nonresident/emot/poser_pose01_", files[2].GamePath);

        // The body clip loads its own facial library.
        var pair = IdlePapBuilder.BuildClip(new(Pap()), new byte[8], 70, "emot/poser_pose01_loop");
        Assert.Contains("emot/poser_pose01_loop", Encoding.ASCII.GetString(pair));

        // A chosen slot and durations apply to body and face together.
        var faceBytes = Pap();
        BinaryPrimitives.WriteInt32LittleEndian(faceBytes.AsSpan(62), 1);
        files = IdlePapBuilder.Files("c0101", "f0002", new(Pap()), new(Pap()), new(faceBytes),
            new byte[8], new byte[8], new byte[8], new byte[8], 4, 90, 120);
        Assert.All(files, file => Assert.Contains("pose04_", file.GamePath));
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
