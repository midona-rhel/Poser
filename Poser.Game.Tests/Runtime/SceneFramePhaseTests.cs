using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Game.Runtime;

namespace Poser.Game.Tests.Runtime;

public sealed unsafe class SceneFramePhaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_owner_pumps_anchors_and_dispose_releases_callbacks(bool renderHookAvailable)
    {
        var framework = Substitute.For<IFramework>();
        var calls = new List<string>();
        var phases = new SceneFramePhaseService(framework, Substitute.For<IPluginLog>(),
            () => calls.Add("anchor"), renderHookAvailable);
        phases.CameraUpdate += _ => calls.Add("camera");
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        if (renderHookAvailable)
        {
            Assert.Empty(calls);
            phases.RunScenePhase(null);
            Assert.Equal(new[] { "anchor", "camera" }, calls);
        }
        else Assert.Equal(new[] { "anchor" }, calls);
        phases.Dispose();
        calls.Clear();
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        phases.RunScenePhase(null);
        Assert.Empty(calls);
    }

    [Fact]
    public void Anchor_failure_does_not_skip_the_camera_phase()
    {
        var log = Substitute.For<IPluginLog>();
        using var phases = new SceneFramePhaseService(Substitute.For<IFramework>(), log,
            () => throw new InvalidOperationException("native write failed"), true);
        bool cameraUpdated = false;
        phases.CameraUpdate += _ => cameraUpdated = true;
        phases.RunScenePhase(null);
        Assert.True(cameraUpdated);
    }
}
