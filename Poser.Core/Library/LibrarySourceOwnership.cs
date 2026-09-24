using System;
using System.IO;
using System.Linq;

namespace Poser.Library;

// Zero is deliberately legacy: old JSON must not acquire ownership by name.
public enum LibrarySourceKind { Legacy, Custom, PoserPoses, PoserScenes, PoserMcdfs, PoserObjects, Brio, Anamnesis }

public partial class LibraryConfiguration
{
    public static bool IsManaged(LibrarySourceKind kind) =>
        kind is LibrarySourceKind.PoserPoses or LibrarySourceKind.PoserScenes
            or LibrarySourceKind.PoserMcdfs or LibrarySourceKind.PoserObjects;

    public static LibrarySourceKind HomeKind(string name) => name switch
    {
        PoseSourceName => LibrarySourceKind.PoserPoses,
        SceneSourceName => LibrarySourceKind.PoserScenes,
        McdfSourceName or "Poser MCDFs" => LibrarySourceKind.PoserMcdfs,
        ObjectsSourceName => LibrarySourceKind.PoserObjects,
        _ => LibrarySourceKind.Custom,
    };

    public static string HomeLeaf(LibrarySourceKind kind) => kind switch
    {
        LibrarySourceKind.PoserPoses => PosesLeaf,
        LibrarySourceKind.PoserScenes => ScenesLeaf,
        LibrarySourceKind.PoserMcdfs => McdfLeaf,
        LibrarySourceKind.PoserObjects => ObjectsLeaf,
        _ => throw new ArgumentException("Not a managed home.", nameof(kind)),
    };

    public LibrarySourceKind Classify(LibrarySourceConfig source) =>
        Classify(source, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    internal LibrarySourceKind Classify(LibrarySourceConfig source, string documents)
    {
        if (source.Kind != LibrarySourceKind.Legacy)
            return Enum.IsDefined(source.Kind) ? source.Kind : LibrarySourceKind.Custom;
        var path = NormalizePath(source.Path);
        if (path is null)
            return LibrarySourceKind.Custom;
        var kind = LegacyNameKind(source.Name);
        if (kind == LibrarySourceKind.Custom)
            return kind;
        // Duplicate/conflicting identities never confer ownership. This also
        // protects a legacy record beside a new explicitly owned seed.
        if (Sources.Count(s => LegacyNameKind(s.Name) == kind && SamePath(s.Path, path)) != 1
            || Sources.Any(s => s.Kind == kind))
            return LibrarySourceKind.Custom;
        var expected = IsManaged(kind)
            ? Path.Combine(documents, "Poser", HomeLeaf(kind))
            : Path.Combine(documents, kind == LibrarySourceKind.Brio ? "Brio" : "Anamnesis", "Poses");
        if (SamePath(path, expected))
            return kind;
        if (!IsManaged(kind) || !string.Equals(Path.GetFileName(path), HomeLeaf(kind), StringComparison.OrdinalIgnoreCase))
            return LibrarySourceKind.Custom;

        // An old custom root is identifiable only as one complete, unambiguous
        // quartet. Incomplete layouts and multiple candidate roots stay custom.
        var roots = Sources.Where(s => s.Kind == LibrarySourceKind.Legacy && HomeKind(s.Name) == LibrarySourceKind.PoserPoses)
            .Select(s => NormalizePath(s.Path)).Where(p => p is not null)
            .Select(p => Path.GetDirectoryName(p!)).Where(p => p is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => Homes.All(home => Sources.Count(s =>
                s.Kind == LibrarySourceKind.Legacy && HomeKind(s.Name) == HomeKind(home.Name)
                && SamePath(s.Path, Path.Combine(root!, HomeLeaf(HomeKind(home.Name))))) == 1))
            .ToArray();
        return roots.Length == 1 && SamePath(Path.GetDirectoryName(path)!, roots[0]!)
            ? kind : LibrarySourceKind.Custom;
    }

    private static LibrarySourceKind LegacyNameKind(string name) => name switch
    {
        "Brio Poses" => LibrarySourceKind.Brio,
        "Anamnesis Poses" => LibrarySourceKind.Anamnesis,
        _ => HomeKind(name),
    };

    internal static bool SamePath(string left, string right) =>
        NormalizePath(left) is { } a && NormalizePath(right) is { } b
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception) { return null; }
    }
}
