using Poser.Domain.Operations;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class SessionAppearanceFilesTests
{
    [Fact]
    public void Embedded_package_survives_history_and_is_removed_only_with_its_session()
    {
        var deleted = new List<string>();
        using var files = new SessionAppearanceFiles(deleted.Add);
        var first = SessionGeneration.New();
        var second = SessionGeneration.New();
        Assert.True(files.Retain("first.mcdf", first));
        files.Sweep(first);
        Assert.Empty(deleted);
        Assert.True(files.Retain("second.mcdf", second));
        files.Sweep(second);
        Assert.Equal("first.mcdf", Assert.Single(deleted));
        files.Dispose();
        Assert.Equal(new[] { "first.mcdf", "second.mcdf" }, deleted);
        Assert.False(files.Retain("late.mcdf", second));
        files.Dispose();
        Assert.Equal(2, deleted.Count);
    }
}
