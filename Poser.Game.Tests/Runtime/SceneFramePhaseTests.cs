using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Game.Runtime;

namespace Poser.Game.Tests.Runtime;

public sealed unsafe class SceneFramePhaseTests
{
    [Fact]
    public void Without_the_render_hook_one_owner_pumps_anchors_and_dispose_releases_callbacks()
    {
        const bool renderHookAvailable = false;
        var framework = Substitute.For<IFramework>();
        var calls = new List<string>();
        var phases = new SceneFramePhaseService(framework, Substitute.For<IPluginLog>(),
            () => calls.Add("anchor"), renderHookAvailable);
        phases.CameraUpdate += _ => calls.Add("camera");
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        Assert.Equal(new[] { "anchor" }, calls);
        phases.Dispose();
        calls.Clear();
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        phases.RunScenePhase(null);
        Assert.Empty(calls);
    }

    [Fact]
    public void Anchors_run_before_camera_and_a_failure_does_not_skip_the_camera_phase()
    {
        var calls = new List<string>();
        using var phases = new SceneFramePhaseService(Substitute.For<IFramework>(), Substitute.For<IPluginLog>(),
            () =>
            {
                calls.Add("anchor");
                throw new InvalidOperationException("native write failed");
            }, true);
        phases.CameraUpdate += _ => calls.Add("camera");
        phases.RunScenePhase(null);
        Assert.Equal(new[] { "anchor", "camera" }, calls);
    }
}
