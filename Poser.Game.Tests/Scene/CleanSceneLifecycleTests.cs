using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class CleanSceneLifecycleTests
{
    [Theory]
    [InlineData("GPose exited.")]
    [InlineData("Scene lifecycle disposed.")]
    public void Lifecycle_teardown_releases_facial_capture_before_reset_all(
        string reason)
    {
        var calls = new List<string>();

        CleanSceneLifecycle.ResetOwnedStateForLifecycle(
            reason,
            detail =>
            {
                Assert.Equal(reason, detail);
                calls.Add("face");
            },
            () => calls.Add("animation"),
            () => calls.Add("presentation"),
            () => calls.Add("integration"));

        Assert.Equal(
            new[] { "face", "animation", "presentation", "integration" },
            calls);
    }
}
