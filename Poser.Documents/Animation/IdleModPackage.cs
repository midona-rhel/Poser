using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Poser.Documents.Animation;

public sealed record IdleModFile(string GamePath, byte[] Bytes);
public sealed record IdleModPackage(string Name, string Description, IReadOnlyList<IdleModFile> Files)
{
    public void WriteNew(string destination)
    {
        if (string.IsNullOrWhiteSpace(Name) || Files.Count is < 1 or > 16)
            throw new InvalidDataException("An idle mod must have a name and bounded file set.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
            if (!Regex.IsMatch(file.GamePath, @"^chara/human/c[0-9]{4}/animation/[af][0-9]{4}/[a-z0-9_/-]+\.pap$", RegexOptions.CultureInvariant) ||
                file.GamePath.Contains("//", StringComparison.Ordinal) || !paths.Add(file.GamePath) || file.Bytes.Length is < 26 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid or duplicate idle mod resource.");
        var path = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(path), ".pmp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .pmp destination.", nameof(destination));
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var temporary = Path.Combine(directory, $".poser-idle-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var map = new Dictionary<string, string>();
                for (int i = 0; i < Files.Count; i++)
                {
                    string resource = $"files/idle-{i}.pap";
                    map.Add(Files[i].GamePath, resource);
                    using var entry = zip.CreateEntry(resource, CompressionLevel.Optimal).Open();
                    entry.Write(Files[i].Bytes);
                }
                Json("meta.json", new { FileVersion = 3, Name, Author = "", Description, Version = "1.0", Website = "", ModTags = new[] { "Animation", "Pose" } });
                Json("default_mod.json", new { Files = map, FileSwaps = new Dictionary<string, string>(), Manipulations = Array.Empty<object>() });
                void Json(string name, object value) { using var entry = zip.CreateEntry(name).Open(); JsonSerializer.Serialize(entry, value); }
            }
            // No overwrite or backup: an existing mod export remains untouched.
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
