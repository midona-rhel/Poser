using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Documents.Files;
using Poser.Domain.Transforms;

namespace Poser.Documents.AutoSave;

/// <summary>Whole-scene snapshot filenames and disk retention, separate from pose snapshots.</summary>
public sealed class SceneAutoSaveStore
{
    private static readonly JsonSerializerOptions SignatureOptions = CreateSignatureOptions();
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
    /// bone or prop would hash the same as before. Bone transforms alone use
    /// semantic buckets: capture/decomposition noise below the pose system's
    /// tolerance is not authored work, while every other field remains exact.
    /// The file written to disk is never rounded.</para>
    /// </summary>
    public static string? Signature(SceneFile scene)
    {
        var savedAt = scene.SavedAt;
        scene.SavedAt = null;
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256
                .HashData(System.Text.Json.JsonSerializer
                    .SerializeToUtf8Bytes(scene, SignatureOptions)));
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

    private static JsonSerializerOptions CreateSignatureOptions()
    {
        var options = new JsonSerializerOptions(SceneFile.JsonOptions);
        options.Converters.Insert(0, new BoneSignatureConverter());
        return options;
    }

    /// <summary>One bucket spans twice the established pose comparison
    /// tolerance, so its maximum canonicalization error is that tolerance.
    /// Bucket identity is used directly by SHA-256: equality and hashing
    /// therefore cannot disagree at an epsilon boundary.</summary>
    private sealed class BoneSignatureConverter : JsonConverter<PoseFile.BoneData>
    {
        private const float Quantum = 2f * TransformMath.ApproximateSameTolerance;

        public override PoseFile.BoneData Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The autosave signature converter only writes.");

        public override void Write(
            Utf8JsonWriter writer, PoseFile.BoneData value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(nameof(PoseFile.BoneData.Position));
            JsonSerializer.Serialize(writer, Canonical(value.Position), options);
            writer.WritePropertyName(nameof(PoseFile.BoneData.Rotation));
            JsonSerializer.Serialize(writer, Canonical(value.Rotation), options);
            writer.WritePropertyName(nameof(PoseFile.BoneData.Scale));
            JsonSerializer.Serialize(writer, Canonical(value.Scale), options);
            writer.WriteEndObject();
        }

        private static float Component(float value) =>
            MathF.Round(value / Quantum, MidpointRounding.ToEven) * Quantum;

        private static Vector3 Canonical(Vector3 value) =>
            new(Component(value.X), Component(value.Y), Component(value.Z));

        private static Quaternion Canonical(Quaternion value)
        {
            // A quaternion and its negation encode the same orientation.
            if (value.W < 0f || value.W == 0f && (value.Z < 0f ||
                value.Z == 0f && (value.Y < 0f || value.Y == 0f && value.X < 0f)))
                value = new Quaternion(-value.X, -value.Y, -value.Z, -value.W);
            return new Quaternion(
                Component(value.X), Component(value.Y),
                Component(value.Z), Component(value.W));
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
