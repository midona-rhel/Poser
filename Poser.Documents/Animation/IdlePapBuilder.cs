using System.Text;

namespace Poser.Documents.Animation;

internal static class IdlePapBuilder
{
    public static IReadOnlyList<IdleModFile> Files(string race, string face,
        PapAnimationDocument start, PapAnimationDocument loop, PapAnimationDocument expression,
        byte[] bodyEntry, byte[] bodyHold, byte[] faceEntry, byte[] faceHold,
        int slot = 1, int entryFrames = 45, int holdFrames = 70)
    {
        if (slot is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(slot));
        string bodyPrefix = $"chara/human/{race}/animation/a0001/bt_common/emote/pose{slot:00}_";
        string facePrefix = $"chara/human/{race}/animation/{face}/nonresident/";
        string entryLibrary = $"emot/poser_pose{slot:00}_start";
        string holdLibrary = $"emot/poser_pose{slot:00}_loop";
        var facial = expression.FaceClip;
        return [new(bodyPrefix + "start.pap", BuildClip(start, bodyEntry, entryFrames, entryLibrary, start.BodyClip, facial.Name)),
            new(bodyPrefix + "loop.pap", BuildClip(loop, bodyHold, holdFrames, holdLibrary, loop.BodyClip, facial.Name)),
            new(facePrefix + entryLibrary + ".pap", BuildClip(expression, faceEntry, entryFrames, selected: facial)),
            new(facePrefix + holdLibrary + ".pap", BuildClip(expression, faceHold, holdFrames, selected: facial))];
    }

    public static byte[] BuildClip(PapAnimationDocument source, byte[] havok, int frames, string? faceLibrary = null,
        PapAnimationDocument.Clip? selected = null, string expressionName = "cfxf_grin")
    {
        var body = selected ?? (source.Clips.Count == 1 ? source.Clips[0]
            : throw new InvalidDataException("Select a motion from the multi-clip animation template."));
        if (!source.Clips.Contains(body) || body.IsFace && faceLibrary != null)
            throw new InvalidDataException("Invalid animation template selection or nested face library.");
        // The caller supplies an independently encoded, single-binding Havok
        // container. The selected source binding is remapped to output index zero.
        var timeline = Timeline(body.Name, frames, faceLibrary == null ? null : expressionName, faceLibrary);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("pap "u8); writer.Write(0x00020001); writer.Write((short)1);
        writer.Write(source.ModelId); writer.Write(source.ModelType); writer.Write(source.Variant);
        writer.Write(26); writer.Write(66); writer.Write(0);
        Clip(body.Name, body.Type, 0, body.IsFace);
        writer.Write(havok);
        while ((stream.Position & 3) != 0) writer.Write((byte)0);
        int timelineOffset = checked((int)stream.Position);
        writer.Write(timeline);
        stream.Position = 22; writer.Write(timelineOffset);
        var bytes = stream.ToArray();
        _ = new PapAnimationDocument(bytes);
        return bytes;

        void Clip(string name, short type, short binding, bool face)
        {
            var text = Encoding.ASCII.GetBytes(name);
            if (text.Length is < 1 or > 31) throw new InvalidDataException("Invalid idle motion name.");
            writer.Write(text); writer.Write(new byte[32 - text.Length]);
            writer.Write(type); writer.Write(binding); writer.Write(face ? 1 : 0);
        }
    }

    internal static byte[] Timeline(string motion, int frames, string? expression = null, string? faceLibrary = null)
    {
        if (frames is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(frames));
        if (faceLibrary != null && (expression == null || !System.Text.RegularExpressions.Regex.IsMatch(faceLibrary, @"^emot/[a-z0-9_]+$")))
            throw new ArgumentException("Invalid face library path.");
        if (string.IsNullOrEmpty(motion) || motion.Any(c => c < 32 || c > 126) ||
            expression is { } e && (e.Length == 0 || e.Any(c => c < 32 || c > 126)))
            throw new ArgumentException("Invalid timeline motion name.");
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.ASCII, true);
        var lists = new List<(long Field, long Origin, short[] Values)>();
        var strings = new List<(long Field, long Origin, string Value)>();
        w.Write("TMLB"u8); w.Write(0); w.Write((expression == null ? 5 : 7) + (faceLibrary == null ? 0 : 1));
        Item("TMDH", 16); w.Write((short)1); w.Write((short)0); w.Write((short)frames); w.Write((short)3);
        if (faceLibrary != null) { var library = Item("TMPP", 12); String(library, faceLibrary); }
        var root = Item("TMAL", 16); List(root, [2]);
        var actor = Item("TMAC", 28); Id(2); w.Write(0); w.Write(0); List(actor, expression == null ? [3] : [3, 4]);
        var track = Item("TMTR", 24); Id(3); List(track, [expression == null ? (short)4 : (short)5]); w.Write(0);
        if (expression != null) { var faceTrack = Item("TMTR", 24); Id(4); List(faceTrack, [6]); w.Write(0); }
        var anim = Item("C009", 24); Id(expression == null ? (short)4 : (short)5); w.Write(frames); w.Write(0); String(anim, motion);
        if (expression != null)
        {
            var face = Item("C010", 40); Id(6); w.Write(frames); w.Write(0);
            w.Write(1); w.Write(0f); w.Write((float)frames); String(face, expression); w.Write(0);
        }
        foreach (var list in lists) { Patch(list.Field, checked((int)(stream.Position - list.Origin))); foreach (var id in list.Values) w.Write(id); }
        foreach (var str in strings) { Patch(str.Field, checked((int)(stream.Position - str.Origin))); w.Write(Encoding.ASCII.GetBytes(str.Value)); w.Write((byte)0); }
        Patch(4, checked((int)stream.Length));
        return stream.ToArray();

        // TMB offsets are relative to their ITEM's payload start, not the offset field.
        long Item(string type, int size) { var pos = stream.Position; w.Write(Encoding.ASCII.GetBytes(type)); w.Write(size); return pos + 8; }
        void Id(short id) { w.Write(id); w.Write((short)0); }
        void List(long origin, short[] ids) { lists.Add((stream.Position, origin, ids)); w.Write(0); w.Write(ids.Length); }
        void String(long origin, string value) { strings.Add((stream.Position, origin, value)); w.Write(0); }
        void Patch(long field, int value) { long end = stream.Position; stream.Position = field; w.Write(value); stream.Position = end; }
    }
}
