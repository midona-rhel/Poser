using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Poser.UI;

/// <summary>
/// Resolves the CJK fallback face the way Chromium falls back from
/// Segoe UI on Windows: by walking the Segoe UI font-link chain
/// (<c>FontLink\SystemLink</c>) in registry order and taking the first
/// linked face that actually covers Japanese — Meiryo UI before
/// Yu Gothic UI on machines that ship both. The SAME resolver runs in
/// the game plugin and in the conformance capture host, so both merge
/// the identical file, face, weight, and metrics.
/// </summary>
public static class WindowsFontFallback
{
    public readonly record struct ResolvedFace(
        string Path, int FaceIndex, int WeightClass, string Family);

    private const string SystemLinkKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontLink\SystemLink";

    // The documented Windows chain for Segoe UI, used when the registry
    // is unreadable; the registry order wins whenever it is available.
    private static readonly (string File, string Family)[] StaticChain =
    [
        ("MEIRYO.TTC", "Meiryo UI"),
        ("YuGothM.ttc", "Yu Gothic UI"),
        ("msgothic.ttc", "MS UI Gothic"),
    ];

    private static readonly object Gate = new();
    private static readonly Dictionary<int, ResolvedFace?> Cache = new();

    /// <summary>First font-link face covering Japanese, nearest to the
    /// requested weight among that file's matching faces. Font-link does
    /// not switch files per weight, so a single-weight family (Meiryo)
    /// serves every weight, exactly as GDI/Chromium fall back.</summary>
    public static ResolvedFace? ResolveJapanese(int weight)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(weight, out var cached))
                return cached;
            var resolved = Resolve(weight);
            Cache[weight] = resolved;
            return resolved;
        }
    }

    private static ResolvedFace? Resolve(int weight)
    {
        string fontsDir = FontsDirectory();
        foreach (var (file, family) in SegoeLinkChain())
        {
            string path = Path.Combine(fontsDir, file);
            if (!File.Exists(path))
                continue;
            ResolvedFace? best = null;
            foreach (var face in EnumerateFaces(path))
            {
                if (family.Length > 0 && !face.FamilyName.StartsWith(
                        family, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!face.CoversJapanese)
                    continue;
                var candidate = new ResolvedFace(
                    path, face.FaceIndex, face.WeightClass, face.FamilyName);
                if (best is null
                    || Math.Abs(candidate.WeightClass - weight)
                        < Math.Abs(best.Value.WeightClass - weight))
                    best = candidate;
            }
            if (best is not null)
                return best;
        }
        return null;
    }

    /// <summary>The Segoe UI SystemLink entries ("FILE,Face[,...]") in
    /// registry order, falling back to the documented static chain.</summary>
    private static IEnumerable<(string File, string Family)> SegoeLinkChain()
    {
        string[]? links = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemLinkKey);
            links = key?.GetValue("Segoe UI") as string[];
        }
        catch
        {
            // Registry unavailable (e.g. Wine) — static chain below.
        }
        if (links is not { Length: > 0 })
        {
            foreach (var entry in StaticChain)
                yield return entry;
            yield break;
        }
        foreach (var link in links)
        {
            var parts = link.Split(',');
            if (parts.Length == 0 || parts[0].Length == 0)
                continue;
            yield return (
                parts[0].Trim(),
                parts.Length > 1 ? parts[1].Trim() : string.Empty);
        }
    }

    private static string FontsDirectory()
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }
        catch
        {
        }
        return @"C:\Windows\Fonts";
    }

    private readonly record struct Face(
        int FaceIndex, string FamilyName, int WeightClass, bool CoversJapanese);

    /// <summary>Enumerates TTF/TTC faces with their Windows (platform 3,
    /// en-US) family name, OS/2 weight class, and whether the cmap
    /// covers Japanese (hiragana A, U+3042).</summary>
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
            int nameOffset = -1, os2Offset = -1, cmapOffset = -1;
            for (int i = 0; i < numTables; i++)
            {
                int record = origin + 12 + i * 16;
                uint tableTag = ReadU32(data, record);
                int offset = (int)ReadU32(data, record + 8);
                if (tableTag == 0x6e616d65) nameOffset = offset; // 'name'
                if (tableTag == 0x4f532f32) os2Offset = offset;  // 'OS/2'
                if (tableTag == 0x636d6170) cmapOffset = offset; // 'cmap'
            }
            if (nameOffset < 0 || os2Offset < 0 || cmapOffset < 0)
                return null;
            string? family = ReadFamilyName(data, nameOffset);
            if (family == null)
                return null;
            return new Face(
                faceIndex,
                family,
                ReadU16(data, os2Offset + 4),
                MapsCodepoint(data, cmapOffset, 0x3042));
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
            if (ReadU16(data, record) != 3
                || ReadU16(data, record + 4) != 0x0409
                || ReadU16(data, record + 6) != 1)
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

    private static bool MapsCodepoint(byte[] data, int cmapOffset, int codepoint)
    {
        ushort tables = ReadU16(data, cmapOffset + 2);
        int subtable = -1;
        for (int i = 0; i < tables; i++)
        {
            int record = cmapOffset + 4 + i * 8;
            if (ReadU16(data, record) == 3
                && ReadU16(data, record + 2) is 1 or 10)
            {
                subtable = cmapOffset + (int)ReadU32(data, record + 4);
                break;
            }
        }
        if (subtable < 0)
            return false;
        ushort format = ReadU16(data, subtable);
        if (format == 4)
        {
            int segments = ReadU16(data, subtable + 6) / 2;
            for (int i = 0; i < segments; i++)
            {
                int end = ReadU16(data, subtable + 14 + i * 2);
                if (codepoint > end)
                    continue;
                int start = ReadU16(data, subtable + 16 + segments * 2 + i * 2);
                if (codepoint < start)
                    return false;
                int rangeOffsetAddress = subtable + 16 + segments * 6 + i * 2;
                int rangeOffset = ReadU16(data, rangeOffsetAddress);
                if (rangeOffset == 0)
                    return true;
                int glyphAddress =
                    rangeOffsetAddress + rangeOffset + (codepoint - start) * 2;
                return ReadU16(data, glyphAddress) != 0;
            }
            return false;
        }
        if (format == 12)
        {
            uint groups = ReadU32(data, subtable + 12);
            for (uint i = 0; i < groups; i++)
            {
                int record = subtable + 16 + (int)i * 12;
                uint start = ReadU32(data, record);
                uint end = ReadU32(data, record + 4);
                if (codepoint >= start && codepoint <= end)
                    return true;
            }
            return false;
        }
        return false;
    }

    private static ushort ReadU16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
