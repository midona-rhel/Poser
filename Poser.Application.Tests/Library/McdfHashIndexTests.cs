using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Poser.Library;

namespace Poser.Tests.Library;

/// <summary>
/// The checksum index answers by CONTENT, remembers what it read, and forgets
/// the moment the file it read changes underneath it.
/// </summary>
public sealed class McdfHashIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poser-mcdf-index-{Guid.NewGuid():N}");

    public McdfHashIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not a failed test.
        }
    }

    [Fact]
    public void A_package_replaced_in_place_cannot_serve_its_old_digest()
    {
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5, 6, 7 };
        string path = Path.Combine(_root, "package.mcdf");
        File.WriteAllBytes(path, first);

        var index = new McdfHashIndex(() => _root);
        Assert.Equal(path, index.Find(Digest(first), Token));

        // Same path, different bytes: the cache is keyed on the identity it
        // read from, so the stale digest must stop matching and the new one
        // must start.
        File.WriteAllBytes(path, second);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.Null(index.Find(Digest(first), Token));
        Assert.Equal(path, index.Find(Digest(second), Token));
    }

    /// <summary>The ambient test token, so a cancelled run stops inside a
    /// multi-megabyte hash instead of after it.</summary>
    private static CancellationToken Token =>
        TestContext.Current.CancellationToken;

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
