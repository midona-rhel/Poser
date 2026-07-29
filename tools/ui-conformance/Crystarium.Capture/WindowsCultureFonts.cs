using System.Globalization;

namespace Crystarium.Capture;

/// <summary>
/// Resolves the Windows default UI font for a culture the way Dalamud's
/// <c>AttachWindowsDefaultFont</c> does — by the Microsoft
/// international-font list family (Japanese → "Yu Gothic UI") matched by
/// name and nearest weight — so the capture host merges exactly the
/// fallback the in-game font path merges, no better and no worse.
/// Returns null for unsupported cultures, mirroring Dalamud's silent
/// no-op.
/// </summary>
internal static class WindowsCultureFonts
{
    internal readonly record struct ResolvedFace(
        string Path, int FaceIndex, int WeightClass);

    private static readonly Dictionary<string, (string Family, string[] Files)>
        CultureFamilies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ja"] = ("Yu Gothic UI",
            [
                "YuGothM.ttc", "YuGothB.ttc", "YuGothR.ttc", "YuGothL.ttc",
            ]),
        };

    public static ResolvedFace? Resolve(CultureInfo culture, int weight)
    {
        if (!CultureFamilies.TryGetValue(
                culture.TwoLetterISOLanguageName, out var entry))
            return null;
        string fontsDir;
        try
        {
            fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(fontsDir))
                fontsDir = @"C:\Windows\Fonts";
        }
        catch
        {
            fontsDir = @"C:\Windows\Fonts";
        }

        ResolvedFace? best = null;
        foreach (var file in entry.Files)
        {
            string path = System.IO.Path.Combine(fontsDir, file);
            if (!File.Exists(path))
                continue;
            foreach (var face in EnumerateFaces(path))
            {
                if (!face.FamilyName.StartsWith(
                        entry.Family, StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = new ResolvedFace(
                    path, face.FaceIndex, face.WeightClass);
                if (best is null
                    || Math.Abs(candidate.WeightClass - weight)
                        < Math.Abs(best.Value.WeightClass - weight))
                    best = candidate;
            }
        }
        return best;
    }

    private readonly record struct Face(
        int FaceIndex, string FamilyName, int WeightClass);

    /// <summary>Enumerates the faces of a TTF/TTC with their Windows
    /// (platform 3, en-US) family name and OS/2 weight class.</summary>
    private static IEnumerable<Face> EnumerateFaces(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch
        {
            yield break;
        }
        uint tag = ReadU32(data, 0);
        uint[] offsets;
        if (tag == 0x74746366) // 'ttcf'
        {
            uint count = ReadU32(data, 8);
            offsets = new uint[count];
            for (uint i = 0; i < count; i++)
                offsets[i] = ReadU32(data, 12 + (int)i * 4);
        }
        else
        {
            offsets = [0];
        }
        for (int faceIndex = 0; faceIndex < offsets.Length; faceIndex++)
        {
            var face = ReadFace(data, (int)offsets[faceIndex], faceIndex);
            if (face is { } value)
                yield return value;
        }
    }

    private static Face? ReadFace(byte[] data, int origin, int faceIndex)
    {
        try
        {
            ushort numTables = ReadU16(data, origin + 4);
            int nameOffset = -1, os2Offset = -1;
            for (int i = 0; i < numTables; i++)
            {
                int record = origin + 12 + i * 16;
                uint tableTag = ReadU32(data, record);
                int offset = (int)ReadU32(data, record + 8);
                if (tableTag == 0x6e616d65) nameOffset = offset; // 'name'
                if (tableTag == 0x4f532f32) os2Offset = offset;  // 'OS/2'
            }
            if (nameOffset < 0 || os2Offset < 0)
                return null;
            string? family = ReadFamilyName(data, nameOffset);
            if (family == null)
                return null;
            int weightClass = ReadU16(data, os2Offset + 4);
            return new Face(faceIndex, family, weightClass);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFamilyName(byte[] data, int nameOffset)
    {
        ushort count = ReadU16(data, nameOffset + 2);
        int stringsOffset = nameOffset + ReadU16(data, nameOffset + 4);
        for (int i = 0; i < count; i++)
        {
            int record = nameOffset + 6 + i * 12;
            ushort platform = ReadU16(data, record);
            ushort language = ReadU16(data, record + 4);
            ushort nameId = ReadU16(data, record + 6);
            if (platform != 3 || language != 0x0409 || nameId != 1)
                continue;
            int length = ReadU16(data, record + 8);
            int offset = stringsOffset + ReadU16(data, record + 10);
            var chars = new char[length / 2];
            for (int c = 0; c < chars.Length; c++)
                chars[c] = (char)ReadU16(data, offset + c * 2);
            return new string(chars);
        }
        return null;
    }

    private static ushort ReadU16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
