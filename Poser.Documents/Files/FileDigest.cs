using System;
using System.IO;
using System.Security.Cryptography;

namespace Poser.Documents.Files;

/// <summary>The one content digest Poser records for files: SHA-256, upper-case hex,
/// streamed so multi-hundred-MB packages are never held in memory.</summary>
public static class FileDigest
{
    public static string Sha256Hex(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream));

    /// <summary>The file's digest, or null when it cannot be read.</summary>
    public static string? TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Sha256Hex(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
