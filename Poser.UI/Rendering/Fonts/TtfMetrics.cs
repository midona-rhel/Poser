using System;
using System.Collections.Generic;
using System.IO;

namespace Poser.UI;

/// <summary>
/// Reads the vertical metrics of a TrueType font to convert CSS pixel sizes to
/// ImGui pixel sizes.
///
/// <para>ImGui/stb sizes fonts so that (hhea.ascender − hhea.descender) equals the
/// requested pixel size; CSS sizes fonts so the em square equals it. For Segoe UI
/// that ratio is ≈1.33 — an ImGui "13px" request renders ≈9.8 CSS px, which made
/// every label ~25% smaller than the design reference (visible as a
/// 44px-vs-57px text advance). Multiply CSS px by <see cref="CssScale"/> before
/// handing sizes to AddFontFromFile.</para>
/// </summary>
public static class TtfMetrics
{
    private static readonly Dictionary<string, float> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(ascender − descender) / unitsPerEm for a face of the
    /// font file; 1.0 when unreadable. TTC collections address the face
    /// by index — faces in one collection can carry different vertical
    /// metrics (Meiryo vs Meiryo UI).</summary>
    public static float CssScale(string path, int faceIndex = 0)
    {
        string key = faceIndex == 0 ? path : $"{path}#{faceIndex}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            float scale = ReadScale(path, faceIndex);
            _cache[key] = scale;
            return scale;
        }
    }

    private static float ReadScale(string path, int faceIndex)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);

            uint tag = ReadU32(r);
            if (tag == 0x74746366) // 'ttcf' — TrueType collection: seek the face
            {
                r.ReadUInt32(); // version
                uint numFonts = ReadU32(r);
                if (numFonts == 0 || faceIndex >= numFonts) return 1f;
                fs.Seek(12 + faceIndex * 4, SeekOrigin.Begin);
                uint faceOffset = ReadU32(r);
                fs.Seek(faceOffset, SeekOrigin.Begin);
                ReadU32(r); // sfnt version of the face
            }
            else if (faceIndex != 0)
            {
                return 1f;
            }

            ushort numTables = ReadU16(r);
            r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt16(); // searchRange/entrySelector/rangeShift

            long headOffset = -1, hheaOffset = -1;
            for (int i = 0; i < numTables; i++)
            {
                uint tableTag = ReadU32(r);
                r.ReadUInt32(); // checksum
                uint offset = ReadU32(r);
                r.ReadUInt32(); // length
                if (tableTag == 0x68656164) headOffset = offset; // 'head'
                if (tableTag == 0x68686561) hheaOffset = offset; // 'hhea'
            }
            if (headOffset < 0 || hheaOffset < 0) return 1f;

            fs.Seek(headOffset + 18, SeekOrigin.Begin);
            ushort unitsPerEm = ReadU16(r);
            fs.Seek(hheaOffset + 4, SeekOrigin.Begin);
            short ascender = (short)ReadU16(r);
            short descender = (short)ReadU16(r);

            if (unitsPerEm == 0) return 1f;
            float scale = (ascender - descender) / (float)unitsPerEm;
            return scale > 0.5f && scale < 3f ? scale : 1f;
        }
        catch
        {
            return 1f; // unreadable font — fall back to ImGui semantics
        }
    }

    private static ushort ReadU16(BinaryReader r)
    {
        var b = r.ReadBytes(2);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static uint ReadU32(BinaryReader r)
    {
        var b = r.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }
}
