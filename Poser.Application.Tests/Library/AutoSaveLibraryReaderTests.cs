using System;
using System.IO;
using Poser.Files;
using Poser.Library;
using Poser.Tests.Files;

namespace Poser.Tests.Library;

/// <summary>
/// The auto-save listing reopens a file only when the listing says it changed,
/// and tells the grid which files have an image worth asking for.
/// </summary>
public sealed class AutoSaveLibraryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poser-autosave-reader-{Guid.NewGuid():N}");

    public AutoSaveLibraryReaderTests() => Directory.CreateDirectory(_root);

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
    public void A_file_is_reopened_only_when_its_length_or_write_time_moves()
    {
        var day = Directory.CreateDirectory(Path.Combine(_root, "2026-08-14")).FullName;
        string path = Path.Combine(day, "12-00-00 Actor.pose");
        var pose = PoseFilePersistenceTests.ValidPose();
        pose.PlaceName = "Limsa Lominsa";
        Assert.True(AtomicPoseFileStore.Default.Write(pose, path).Succeeded);
        var written = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        var reader = new AutoSaveLibraryReader();
        Assert.Equal("Limsa Lominsa", Single(reader).Place);

        // Same length, same stamp, different bytes: the pass must answer from
        // what it remembered rather than opening the file again.
        long length = new FileInfo(path).Length;
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal("Limsa Lominsa", Single(reader).Place);

        // A moved stamp is a different file: it is read again, and this one
        // no longer records a place.
        File.SetLastWriteTimeUtc(path, written.AddSeconds(5));
        Assert.Equal(string.Empty, Single(reader).Place);
    }

    [Fact]
    public void Only_a_file_that_embeds_an_image_claims_a_thumbnail()
    {
        var day = Directory.CreateDirectory(Path.Combine(_root, "2026-08-14")).FullName;
        Assert.True(AtomicPoseFileStore.Default.Write(
            PoseFilePersistenceTests.ValidPose(),
            Path.Combine(day, "12-00-00 Plain.pose")).Succeeded);
        var pictured = PoseFilePersistenceTests.ValidPose();
        pictured.Base64Image = "thumbnail";
        Assert.True(AtomicPoseFileStore.Default.Write(
            pictured, Path.Combine(day, "12-00-00 Pictured.pose")).Succeeded);

        var entries = Assert.Single(new AutoSaveLibraryReader().Read(_root)).Entries;

        Assert.False(entries.Find(entry => entry.Name.EndsWith("Plain", StringComparison.Ordinal))!.HasThumbnail);
        Assert.True(entries.Find(entry => entry.Name.EndsWith("Pictured", StringComparison.Ordinal))!.HasThumbnail);
    }

    private AutoSaveEntry Single(AutoSaveLibraryReader reader) =>
        Assert.Single(Assert.Single(reader.Read(_root)).Entries);
}
