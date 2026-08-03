using System;
using System.Collections.Generic;
using System.IO;

namespace Poser.UI;

/// <summary>
/// One face's vertical metrics, normalized to the em square. Descent is
/// NEGATIVE (font-file convention). <see cref="Valid"/> is false when the
/// file could not be parsed — callers fall back rather than trusting zeros.
/// </summary>
public readonly record struct FaceMetrics
{
    public float AscentEm { get; init; }
    public float DescentEm { get; init; }
    public float CapHeightEm { get; init; }
    public int UnitsPerEm { get; init; }
    public bool Valid { get; init; }

    /// <summary>Line box as a fraction of the em square — the ratio ImGui
    /// sizes by.</summary>
    public float LineHeightEm => AscentEm - DescentEm;
}

/// <summary>
/// Reads the vertical metrics of a TrueType font: the CSS-pixel size
/// conversion and the cap height that ink centering seats text by.
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
    private static readonly Dictionary<string, FaceMetrics> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vertical metrics for a face of the font file. TTC
    /// collections address the face by index — faces in one collection can
    /// carry different vertical metrics (Meiryo vs Meiryo UI).</summary>
    public static FaceMetrics Face(string path, int faceIndex = 0)
    {
        string key = faceIndex == 0 ? path : $"{path}#{faceIndex}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var metrics = ReadMetrics(path, faceIndex);
            _cache[key] = metrics;
            return metrics;
        }
    }

    /// <summary>(ascender − descender) / unitsPerEm for a face of the
    /// font file; 1.0 when unreadable or implausible.</summary>
    public static float CssScale(string path, int faceIndex = 0)
    {
        var metrics = Face(path, faceIndex);
        if (!metrics.Valid) return 1f;
        float scale = metrics.LineHeightEm;
        return scale > 0.5f && scale < 3f ? scale : 1f;
    }

    /// <summary>Cap height as a fraction of the em when OS/2 does not
    /// report one (version &lt; 2). Reading it from the 'H' outline means
    /// parsing cmap+loca+glyf; every face this app ships carries a real
    /// sCapHeight, so the miss stays an approximation.</summary>
    private const float ApproxCapHeightEm = 0.7f;

    private static FaceMetrics ReadMetrics(string path, int faceIndex)
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
                if (numFonts == 0 || faceIndex >= numFonts) return default;
                fs.Seek(12 + faceIndex * 4, SeekOrigin.Begin);
                uint faceOffset = ReadU32(r);
                fs.Seek(faceOffset, SeekOrigin.Begin);
                ReadU32(r); // sfnt version of the face
            }
            else if (faceIndex != 0)
            {
                return default;
            }

            ushort numTables = ReadU16(r);
            r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt16(); // searchRange/entrySelector/rangeShift

            long headOffset = -1, hheaOffset = -1, os2Offset = -1;
            uint os2Length = 0;
            for (int i = 0; i < numTables; i++)
            {
                uint tableTag = ReadU32(r);
                r.ReadUInt32(); // checksum
                uint offset = ReadU32(r);
                uint length = ReadU32(r);
                if (tableTag == 0x68656164) headOffset = offset; // 'head'
                if (tableTag == 0x68686561) hheaOffset = offset; // 'hhea'
                if (tableTag == 0x4f532f32) { os2Offset = offset; os2Length = length; } // 'OS/2'
            }
            if (headOffset < 0 || hheaOffset < 0) return default;

            fs.Seek(headOffset + 18, SeekOrigin.Begin);
            ushort unitsPerEm = ReadU16(r);
            fs.Seek(hheaOffset + 4, SeekOrigin.Begin);
            short ascender = (short)ReadU16(r);
            short descender = (short)ReadU16(r);
            if (unitsPerEm == 0) return default;

            // OS/2.sCapHeight is at byte 88 and exists from table version 2.
            float capHeightEm = ApproxCapHeightEm;
            if (os2Offset >= 0 && os2Length >= 90)
            {
                fs.Seek(os2Offset, SeekOrigin.Begin);
                if (ReadU16(r) >= 2)
                {
                    fs.Seek(os2Offset + 88, SeekOrigin.Begin);
                    short capHeight = (short)ReadU16(r);
                    if (capHeight > 0)
                        capHeightEm = capHeight / (float)unitsPerEm;
                }
            }

            return new FaceMetrics
            {
                AscentEm = ascender / (float)unitsPerEm,
                DescentEm = descender / (float)unitsPerEm,
                CapHeightEm = capHeightEm,
                UnitsPerEm = unitsPerEm,
                Valid = true,
            };
        }
        catch
        {
            return default; // unreadable font — callers fall back
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
