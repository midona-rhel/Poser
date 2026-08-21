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
    public void A_package_is_found_by_its_bytes_wherever_it_is_filed()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        string nested = Path.Combine(_root, "sorted", "friends");
        Directory.CreateDirectory(nested);
        string path = Path.Combine(nested, "renamed-since-the-save.mcdf");
        File.WriteAllBytes(path, bytes);

        var index = new McdfHashIndex(() => _root);

        Assert.Equal(path, index.Find(Digest(bytes), Token));
    }

    [Fact]
    public void A_hash_the_library_does_not_hold_answers_nothing()
    {
        File.WriteAllBytes(
            Path.Combine(_root, "other.mcdf"), new byte[] { 9, 9, 9 });
        var index = new McdfHashIndex(() => _root);

        Assert.Null(index.Find(Digest(new byte[] { 1 }), Token));
    }

    [Fact]
    public void A_malformed_hash_and_a_missing_root_answer_without_reading()
    {
        var index = new McdfHashIndex(() => _root);
        Assert.Null(index.Find(string.Empty, Token));
        Assert.Null(index.Find("not-a-digest", Token));

        var missing = new McdfHashIndex(
            () => Path.Combine(_root, "nowhere"));
        Assert.Null(missing.Find(Digest(new byte[] { 1 }), Token));
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

    [Fact]
    public void A_second_lookup_answers_from_the_cache_without_rereading()
    {
        var bytes = new byte[] { 8, 8, 8, 8 };
        string path = Path.Combine(_root, "package.mcdf");
        File.WriteAllBytes(path, bytes);

        var index = new McdfHashIndex(() => _root);
        Assert.Equal(path, index.Find(Digest(bytes), Token));

        // The file is opened exclusively, so a lookup that re-read it would
        // throw or miss. A cached digest whose stamp still holds does neither.
        using var exclusive = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(path, index.Find(Digest(bytes), Token));
    }

    [Fact]
    public void A_cancelled_search_stops_and_answers_nothing()
    {
        var bytes = new byte[] { 3, 3, 3 };
        File.WriteAllBytes(Path.Combine(_root, "package.mcdf"), bytes);

        var index = new McdfHashIndex(() => _root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(index.Find(Digest(bytes), cancelled.Token));
    }

    /// <summary>The ambient test token, so a cancelled run stops inside a
    /// multi-megabyte hash instead of after it.</summary>
    private static CancellationToken Token =>
        TestContext.Current.CancellationToken;

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
