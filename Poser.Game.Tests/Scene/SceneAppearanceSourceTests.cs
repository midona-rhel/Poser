using System.Threading;
using Poser.Files;
using Poser.Game.Scene;
using Poser.Library;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The reference-entry resolution order: content, then location, then a
/// refusal that names both attempts.
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
    public void The_library_match_wins_over_the_recorded_path()
    {
        var index = new FakeIndex { Match = @"D:\mcdfs\filed\away.mcdf" };

        var resolved = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest),
            index, _ => true, Token);

        Assert.Equal(SceneAppearanceOrigin.Library, resolved.Origin);
        Assert.Equal(@"D:\mcdfs\filed\away.mcdf", resolved.Path);
        Assert.Equal(Digest, index.Asked);
        Assert.Contains("matched by checksum", resolved.Detail);
        Assert.Contains(@"D:\mcdfs\filed\away.mcdf", resolved.Detail);
    }

    [Fact]
    public void A_library_match_at_the_recorded_path_says_nothing_extra()
    {
        var index = new FakeIndex { Match = @"C:\old\actor.mcdf" };

        var resolved = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest),
            index, _ => true, Token);

        Assert.Equal(SceneAppearanceOrigin.Library, resolved.Origin);
        Assert.Null(resolved.Detail);
    }

    [Fact]
    public void The_recorded_path_is_the_fallback_when_the_library_misses()
    {
        var resolved = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest),
            new FakeIndex(), _ => true, Token);

        Assert.Equal(SceneAppearanceOrigin.RecordedPath, resolved.Origin);
        Assert.Equal(@"C:\old\actor.mcdf", resolved.Path);
    }

    [Fact]
    public void A_scene_with_no_checksum_never_searches_the_library()
    {
        var index = new FakeIndex { Match = @"D:\mcdfs\away.mcdf" };

        var resolved = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", string.Empty),
            index, _ => true, Token);

        Assert.Null(index.Asked);
        Assert.Equal(SceneAppearanceOrigin.RecordedPath, resolved.Origin);
    }

    [Fact]
    public void Both_missing_refuses_and_names_both_attempts()
    {
        var refused = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", Digest),
            new FakeIndex(), _ => false, Token);

        Assert.Equal(SceneAppearanceOrigin.None, refused.Origin);
        Assert.Null(refused.Path);
        Assert.Contains("no package in your MCDF library matches", refused.Detail);
        Assert.Contains(@"C:\old\actor.mcdf", refused.Detail);
        Assert.Contains("no longer exists", refused.Detail);

        // Without a checksum the refusal must not claim a search happened.
        var unhashed = SceneAppearanceSource.Resolve(
            Entry(@"C:\old\actor.mcdf", string.Empty),
            new FakeIndex(), _ => false, Token);
        Assert.Contains("recorded no checksum", unhashed.Detail);
    }

    private static CancellationToken Token =>
        TestContext.Current.CancellationToken;

    private static SceneActorMcdf Entry(string path, string hash) =>
        new() { Path = path, FileName = "actor.mcdf", ContentHash = hash };
}
