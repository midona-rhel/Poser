#if DEBUG
using System.IO.Compression;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Documents.Animation;

namespace Poser.Game.Animation;

// Read-only native diagnostics for exported files; no actor or animation state is touched.
public static class IdleModDiagnostics
{
    public static object Inspect(string path, string race, string face, IFramework framework, IDataManager data, ISigScanner scanner)
    {
        using var zip = ZipFile.OpenRead(path);
        using var file = zip.GetEntry("files/idle-1.pap")!.Open();
        using var memory = new MemoryStream(); file.CopyTo(memory);
        var pap = new PapAnimationDocument(memory.ToArray());
        var encoder = new IdleHavokEncoder(framework, scanner);
        var sklb = data.GetFile($"chara/human/{race}/skeleton/face/{face}/skl_{race}{face}.sklb")!.Data;
        var neutral = new PapAnimationDocument(data.GetFile($"chara/human/{race}/animation/{face}/resident/face.pap")!.Data);
        var layout = encoder.ReadSkeleton(sklb);
        var actual = encoder.SampleStart(pap.HavokBytes, sklb, pap.Clips.Single(c => c.IsFace).BindingIndex);
        var baseline = encoder.SampleStart(neutral.HavokBytes, sklb, neutral.Clips.Single(c => c.Name == "cfxf_base").BindingIndex);
        return new { clips = pap.Clips, bones = layout.Bones.Select((name, i) => new
        {
            name, positionDelta = Vector3.Distance(actual[i].Position, baseline[i].Position),
            rotationDelta = 1 - Math.Abs(Quaternion.Dot(actual[i].Rotation, baseline[i].Rotation)),
            scaleDelta = Vector3.Distance(actual[i].Scale, baseline[i].Scale),
        }).ToArray() };
    }
}
#endif
