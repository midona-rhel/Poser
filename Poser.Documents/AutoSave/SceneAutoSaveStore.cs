using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Documents.Files;
using Poser.Domain.Transforms;

namespace Poser.Documents.AutoSave;

/// <summary>Opaque comparison baseline retained only after a successful scene autosave.</summary>
public sealed class SceneAutoSaveIdentity
{
    internal SceneAutoSaveIdentity(string exactHash, IReadOnlyList<Transform> bones)
    {
        ExactHash = exactHash;
        Bones = bones;
    }

    internal string ExactHash { get; }
    internal IReadOnlyList<Transform> Bones { get; }
}

/// <summary>Whole-scene snapshot filenames and disk retention, separate from pose snapshots.</summary>
public sealed class SceneAutoSaveStore
{
    private readonly SceneFileStore _store;
    private readonly Action<string> _error;
    public string RootDirectory { get; }

    public SceneAutoSaveStore(string rootDirectory, Action<string> error, SceneFileStore? store = null)
    {
        RootDirectory = rootDirectory;
        _error = error;
        _store = store ?? SceneFileStore.Default;
    }

    /// <summary>
    /// Content identity of one captured scene, with the capture stamp set
    /// aside: two ticks over an untouched scene differ only in when they ran,
    /// and that is not a change worth a file.
    ///
    /// <para>It hashes the WHOLE document, embedded poses included, rather
    /// than a summary of it: a summary that missed a moved bone would drop
    /// the user's work, which is far worse than a duplicate file. A document
    /// this cannot describe answers null, and a null signature never matches
    /// anything — an unreadable scene is written, not skipped.</para>
    ///
    /// <para>It hashes with the document's own wire options: without the
    /// numerics converters every vector serializes as <c>{}</c>, so a moved
    /// bone or prop would hash the same as before.</para>
    /// </summary>
    public static string? Signature(SceneFile scene)
    {
        var savedAt = scene.SavedAt;
        scene.SavedAt = null;
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256
                .HashData(System.Text.Json.JsonSerializer
                    .SerializeToUtf8Bytes(scene, SceneFile.JsonOptions)));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            scene.SavedAt = savedAt;
        }
    }

    /// <summary>Captures an exact hash of every non-bone value plus the bone
    /// transforms in wire order. Comparing to the last successfully written
    /// baseline absorbs sub-tolerance capture noise without hiding cumulative
    /// motion or changing the document that is written.</summary>
    public static SceneAutoSaveIdentity? CaptureIdentity(SceneFile scene)
    {
        var bones = new List<Transform>();
        var options = new JsonSerializerOptions(SceneFile.JsonOptions);
        options.Converters.Insert(0, new BoneIdentityConverter(bones));
        var savedAt = scene.SavedAt;
        scene.SavedAt = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(scene, options);
            return new SceneAutoSaveIdentity(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                bones.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            scene.SavedAt = savedAt;
        }
    }

    public static bool SemanticallyEquals(SceneAutoSaveIdentity left, SceneAutoSaveIdentity right)
    {
        if (!string.Equals(left.ExactHash, right.ExactHash, StringComparison.Ordinal)
            || left.Bones.Count != right.Bones.Count)
            return false;
        for (var i = 0; i < left.Bones.Count; i++)
            if (!Same(left.Bones[i], right.Bones[i]))
                return false;
        return true;
    }

    private static bool Same(Transform left, Transform right)
    {
        const float tolerance = TransformMath.ApproximateSameTolerance;
        static bool Vector(Vector3 a, Vector3 b, float tolerance) =>
            MathF.Abs(a.X - b.X) <= tolerance &&
            MathF.Abs(a.Y - b.Y) <= tolerance &&
            MathF.Abs(a.Z - b.Z) <= tolerance;
        static bool ScaleComponent(float a, float b, float tolerance) =>
            MathF.Abs(a - b) <= tolerance * (1f + MathF.Max(MathF.Abs(a), MathF.Abs(b)));
        static bool Scale(Vector3 a, Vector3 b, float tolerance) =>
            ScaleComponent(a.X, b.X, tolerance) &&
            ScaleComponent(a.Y, b.Y, tolerance) &&
            ScaleComponent(a.Z, b.Z, tolerance);
        if (!Vector(left.Position, right.Position, tolerance)
            // Repeated evaluated-scale composition accumulates relative as
            // well as absolute float error; compare against the saved baseline.
            || !Scale(left.Scale, right.Scale, tolerance))
            return false;
        var aLength = left.Rotation.LengthSquared();
        var bLength = right.Rotation.LengthSquared();
        if (aLength < float.Epsilon || bLength < float.Epsilon)
            return left.Rotation == right.Rotation;
        var a = Quaternion.Normalize(left.Rotation);
        var b = Quaternion.Normalize(right.Rotation);
        if (Quaternion.Dot(a, b) < 0f)
            b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
        return MathF.Abs(a.X - b.X) <= tolerance &&
            MathF.Abs(a.Y - b.Y) <= tolerance &&
            MathF.Abs(a.Z - b.Z) <= tolerance &&
            MathF.Abs(a.W - b.W) <= tolerance;
    }

    private sealed class BoneIdentityConverter(List<Transform> bones) : JsonConverter<PoseFile.BoneData>
    {

        public override PoseFile.BoneData Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The autosave signature converter only writes.");

        public override void Write(
            Utf8JsonWriter writer, PoseFile.BoneData value, JsonSerializerOptions options)
        {
            bones.Add(value);
            // Keys, collection order and every surrounding field remain in
            // the exact hash; only the three noisy transform values move to
            // the baseline-relative comparison above.
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    public (string Path, SceneWriteOutcome Result) Write(SceneFile scene, DateTime localNow)
    {
        var folder = Path.Combine(RootDirectory, localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, localNow);
        return (path, _store.Write(scene, path));
    }

    /// <summary>A second snapshot inside the same second suffixes rather than
    /// overwriting — same convention the pose auto-save uses.</summary>
    private static string UniquePath(string folder, DateTime localNow)
    {
        string stem = localNow.ToString("HH-mm-ss", CultureInfo.InvariantCulture) + " Scene";
        string candidate = System.IO.Path.Combine(
            folder, stem + SceneFile.Extension);
        for (int suffix = 2; File.Exists(candidate) && suffix < 100; suffix++)
        {
            candidate = System.IO.Path.Combine(
                folder, $"{stem} ({suffix}){SceneFile.Extension}");
        }
        return candidate;
    }

    /// <summary>
    /// Retention from DISK, never from an in-memory list: one snapshot file is
    /// one save event, newest first by write date so the order survives a
    /// restart, and a day folder whose last snapshot is pruned goes with it.
    /// Every IO failure is logged with its path and never aborts the sweep.
    /// </summary>
    public void Prune(int keep)
    {
        List<FileInfo> snapshots;
        try
        {
            var root = new DirectoryInfo(RootDirectory);
            if (!root.Exists)
                return;
            snapshots = root
                .EnumerateFiles("*" + SceneFile.Extension, SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.FullName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _error($"Scene auto-save: could not enumerate '{RootDirectory}': {ex.Message}");
            return;
        }

        foreach (var stale in snapshots.Skip(keep))
        {
            try
            {
                stale.Delete();
            }
            catch (Exception ex)
            {
                _error($"Scene auto-save: could not delete '{stale.FullName}': {ex.Message}");
            }
        }

        try
        {
            foreach (var day in new DirectoryInfo(RootDirectory).EnumerateDirectories())
            {
                try
                {
                    if (!day.EnumerateFileSystemInfos().Any())
                        day.Delete();
                }
                catch (Exception ex)
                {
                    _error(
                        $"Scene auto-save: could not remove '{day.FullName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _error($"Scene auto-save: could not sweep '{RootDirectory}': {ex.Message}");
        }
    }

}
