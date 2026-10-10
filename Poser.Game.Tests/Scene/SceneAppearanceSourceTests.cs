using System.Threading;
using Poser.Game.Scene;
using Poser.Documents.Files;
using Poser.Documents.Library;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The reference-entry resolution order: content, then location, then a
/// refusal.
/// </summary>
public sealed class SceneAppearanceSourceTests
{
    private sealed class FakeIndex : IMcdfHashIndex
    {
        public string? Match;
        public string? Asked;

        public string? Find(string contentHash, CancellationToken cancellation = default)
        {
            Asked = contentHash;
            return Match;
        }
    }

    private const string Digest =
        "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void Library_match_wins_then_recorded_path_then_refusal()
    {
        var index = new FakeIndex { Match = @"D:\mcdfs\filed\away.mcdf" };
        var resolved = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest), index, _ => true, Token);
        Assert.Equal(SceneAppearanceOrigin.Library, resolved.Origin);
        Assert.Equal(@"D:\mcdfs\filed\away.mcdf", resolved.Path);
        Assert.Equal(Digest, index.Asked);

        var fallback = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest), new FakeIndex(), _ => true, Token);
        Assert.Equal(SceneAppearanceOrigin.RecordedPath, fallback.Origin);
        Assert.Equal(@"C:\old\actor.mcdf", fallback.Path);

        // A scene with no checksum never queries the library.
        var unasked = new FakeIndex { Match = @"D:\mcdfs\away.mcdf" };
        var unhashed = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", string.Empty), unasked, _ => true, Token);
        Assert.Null(unasked.Asked);
        Assert.Equal(SceneAppearanceOrigin.RecordedPath, unhashed.Origin);

        var refused = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest), new FakeIndex(), _ => false, Token);
        Assert.Equal(SceneAppearanceOrigin.None, refused.Origin);
        Assert.Null(refused.Path);
    }

    private static CancellationToken Token =>
        TestContext.Current.CancellationToken;

    private static SceneActorMcdf Entry(string path, string hash) =>
        new() { Path = path, FileName = "actor.mcdf", ContentHash = hash };
}
