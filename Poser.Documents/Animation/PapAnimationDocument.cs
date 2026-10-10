using System.Buffers.Binary;
using System.Text;

namespace Poser.Documents.Animation;

internal sealed class PapAnimationDocument
{
    internal readonly record struct Clip(string Name, short Type, short BindingIndex, bool IsFace);
    private readonly byte[] _source;
    private readonly int _havokOffset;
    private readonly int _timelineOffset;
    public IReadOnlyList<Clip> Clips { get; }
    public ushort ModelId => BinaryPrimitives.ReadUInt16LittleEndian(_source.AsSpan(10));
    public byte ModelType => _source[12];
    public byte Variant => _source[13];
    public byte[] HavokBytes => _source[_havokOffset.._timelineOffset];

    public Clip BodyClip => Clips.Single(c => !c.IsFace && c.Name.StartsWith("cbem_", StringComparison.Ordinal));
    // The template supplies binding metadata only; all motion samples are replaced.
    public Clip FaceClip => Clips.First(c => c.IsFace);

    public int DurationFrames
    {
        get
        {
            // The first embedded TMB describes the primary motion, even when
            // the PAP also carries secondary motions (e.g. Miqo'te pose 3).
            var timeline = _source.AsSpan(_timelineOffset);
            if (timeline.Length < 28 || !timeline.Slice(12, 4).SequenceEqual("TMDH"u8))
                throw new InvalidDataException("Unsupported idle timeline header.");
            int frames = BinaryPrimitives.ReadInt16LittleEndian(timeline[24..]);
            return frames > 0 ? frames : throw new InvalidDataException("The idle timeline has no duration.");
        }
    }

    public PapAnimationDocument(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 26 or > 64 * 1024 * 1024 || !bytes[..4].SequenceEqual("pap "u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != 0x00020001)
            throw new InvalidDataException("Unsupported PAP header.");
        int count = BinaryPrimitives.ReadInt16LittleEndian(bytes[8..]);
        int info = BinaryPrimitives.ReadInt32LittleEndian(bytes[14..]);
        _havokOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
        _timelineOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
        if (count is < 1 or > 1024 || info < 26 || (long)info + count * 40 > _havokOffset ||
            _havokOffset >= _timelineOffset || _timelineOffset > bytes.Length - 12)
            throw new InvalidDataException("PAP sections overlap or are outside the file.");
        var clips = new Clip[count];
        for (int i = 0; i < count; i++)
        {
            var row = bytes.Slice(info + i * 40, 40);
            int end = row[..32].IndexOf((byte)0);
            if (end <= 0 || row[..end].ContainsAnyExceptInRange((byte)32, (byte)126))
                throw new InvalidDataException("PAP clip has an invalid name.");
            short index = BinaryPrimitives.ReadInt16LittleEndian(row[34..]);
            int face = BinaryPrimitives.ReadInt32LittleEndian(row[36..]);
            if (index < 0 || face is < 0 or > 1)
                throw new InvalidDataException("PAP clip has invalid binding metadata.");
            clips[i] = new(Encoding.ASCII.GetString(row[..end]), BinaryPrimitives.ReadInt16LittleEndian(row[32..]), index, face == 1);
        }
        if (!bytes.Slice(_timelineOffset, 4).SequenceEqual("TMLB"u8))
            throw new InvalidDataException("PAP has no timeline section.");
        _source = bytes.ToArray();
        Clips = Array.AsReadOnly(clips);
    }
}
