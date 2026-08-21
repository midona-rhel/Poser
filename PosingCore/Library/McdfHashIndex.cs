using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Poser.Config;

namespace Poser.Library;

/// <summary>
/// Finds an appearance package in the user's MCDF library BY ITS CONTENT,
/// never by its name or its path.
///
/// <para>It is the seam a scene load uses when the document carries a
/// reference rather than an embedded payload: the file it names may have been
/// renamed, moved into a subfolder, or re-downloaded somewhere else, and none
/// of that changes the bytes. A checksum match is the only evidence that the
/// package on this machine is the package the scene was saved against.</para>
///
/// <para>Stated as an interface because the library side owns MCDFs and will
/// eventually own this index too; nothing here draws UI or decides library
/// policy, so that move costs one registration.</para>
/// </summary>
public interface IMcdfHashIndex
{
    /// <summary>
    /// The path of the library package whose bytes are
    /// <paramref name="contentHash"/> (SHA-256, hex), or null when the library
    /// holds no such package. A hash that is not a SHA-256 digest answers null
    /// without touching the disk.
    ///
    /// <para>Hashing multi-megabyte packages is FILE work: callers run it off
    /// the framework thread and hand it a token, and it stops at the first
    /// match rather than reading the whole library.</para>
    /// </summary>
    string? Find(string contentHash, CancellationToken cancellation = default);
}

/// <summary>
/// The production index over the configured MCDF home.
///
/// <para>It hashes LAZILY — nothing is read until a scene load actually asks
/// for a checksum, and even then it stops at the first file that matches — and
/// remembers each answer against the identity of the file it read it from:
/// path, byte length and last-write time. Any of the three moving re-hashes
/// that file, so a package replaced in place cannot serve its old digest.</para>
///
/// <para>The cache is IN-MEMORY, for the session. The pose library keeps no
/// derived state on disk at all — it rescans — and a checksum cache is the
/// same kind of fact as a scan: cheap to rebuild, wrong to persist past a
/// build that might hash differently. Nothing here is eager; there is no
/// startup pass.</para>
/// </summary>
public sealed class McdfHashIndex : IMcdfHashIndex
{
    /// <summary>Hex characters of a SHA-256 digest.</summary>
    private const int DigestCharacters = 64;

    private const string PackagePattern = "*.mcdf";

    /// <summary>What a cached digest was read FROM. A path is not an identity:
    /// a package replaced in place keeps its path and changes its bytes.
    /// </summary>
    private readonly record struct FileStamp(long Length, long WriteTicks);

    private readonly Func<string> _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, (FileStamp Stamp, string Hash)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The production form: the library's own configured MCDF home,
    /// re-read on every call so re-pointing the home takes effect without a
    /// restart.</summary>
    public McdfHashIndex(ConfigurationService configuration)
        : this(() => configuration.Config.Library.ResolveMcdfRoot())
    {
    }

    /// <summary>Explicit-root form, for a caller that owns the folder.
    /// </summary>
    public McdfHashIndex(Func<string> root) => _root = root;

    public string? Find(string contentHash, CancellationToken cancellation = default)
    {
        if (contentHash is not { Length: DigestCharacters })
            return null;

        string root;
        try
        {
            root = _root();
        }
        catch (Exception)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        List<string> files;
        try
        {
            files = new List<string>(Directory.EnumerateFiles(
                root, PackagePattern, SearchOption.AllDirectories));
        }
        catch (Exception)
        {
            // A library folder that cannot be walked is a library with no
            // answer, not a failed load: the caller still has the recorded
            // path to fall back to.
            return null;
        }

        // Cached files first. A library the user loads scenes out of settles
        // after one pass, and the answer for a hit then costs one stat per
        // file instead of re-reading tens of megabytes.
        if (Match(files, contentHash, cached: true, cancellation) is { } known)
            return known;
        return Match(files, contentHash, cached: false, cancellation);
    }

    /// <summary>One pass over the library. <paramref name="cached"/> selects
    /// which files this pass is willing to answer for: the ones whose digest
    /// is already known and whose stamp still holds, or the rest, which have
    /// to be read.</summary>
    private string? Match(
        List<string> files,
        string contentHash,
        bool cached,
        CancellationToken cancellation)
    {
        foreach (var file in files)
        {
            if (cancellation.IsCancellationRequested)
                return null;
            if (Stamp(file) is not { } stamp)
                continue;

            string? digest;
            lock (_gate)
            {
                bool hit = _cache.TryGetValue(file, out var held) &&
                    held.Stamp == stamp;
                if (hit != cached)
                    continue;
                digest = hit ? held.Hash : null;
            }

            if (digest is null)
            {
                digest = HashFile(file);
                if (digest is null)
                    continue;
                lock (_gate)
                    _cache[file] = (stamp, digest);
            }

            if (string.Equals(
                digest, contentHash, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    /// <summary>The file's identity for cache purposes. A file that cannot be
    /// stat'd is skipped rather than stamped as default, which would make
    /// every unreadable file look like the same file.</summary>
    private static FileStamp? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
