using System;
using System.Collections.Generic;

namespace Poser.Domain.Integration;

/// <summary>
/// MCDF v1 format rules shared by the reader (validation) and the export
/// capture (compatibility filtering). Pure string logic; the wire I/O
/// itself lives at the runtime file boundary.
/// </summary>
public static class McdfFormat
{
    public const byte Version = 1;

    /// <summary>The Brio-compatible resource extensions. Anything else is
    /// rejected on import and skipped (reported) on export.</summary>
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex",
            ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".kdb",
        };

    /// <summary>Lower-case forward-slash form used for every game path.</summary>
    public static string NormalizeGamePath(string path) =>
        path.Trim().Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Validates one normalized game path: relative, inside the game's
    /// virtual tree, and an allowed resource extension. Returns the failure
    /// reason, or null when valid.
    /// </summary>
    public static string? ValidateGamePath(string normalized)
    {
        if (normalized.Length == 0)
            return "an empty game path";
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            return $"a rooted filesystem path ({normalized})";
        foreach (var segment in normalized.Split('/'))
            if (segment == "..")
                return $"a parent-directory segment ({normalized})";
        var dot = normalized.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : normalized[dot..];
        if (!AllowedExtensions.Contains(extension))
            return $"an unsupported resource extension ({normalized})";
        return null;
    }

    /// <summary>
    /// Brio's export compatibility filter, applied after discovery:
    /// animation/sound resources are omitted, and VFX textures travel only
    /// for weapon and equipment paths.
    /// </summary>
    public static bool ExportFilterAllows(string normalizedGamePath)
    {
        if (normalizedGamePath.EndsWith(".pap", StringComparison.Ordinal)
            || normalizedGamePath.EndsWith(".tmb", StringComparison.Ordinal)
            || normalizedGamePath.EndsWith(".scd", StringComparison.Ordinal))
            return false;
        if (normalizedGamePath.EndsWith(".avfx", StringComparison.Ordinal)
            || normalizedGamePath.EndsWith(".atex", StringComparison.Ordinal))
            return normalizedGamePath.Contains("/weapon/", StringComparison.Ordinal)
                || normalizedGamePath.Contains("/equipment/", StringComparison.Ordinal);
        return true;
    }
}
