using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.ContractTests;

public sealed class CompositionContractTests
{
    [Fact]
    public void Failed_activation_disposes_completed_steps_in_reverse_order()
    {
        var events = new List<string>();
        var factories = new Func<FakeActivationResource>[]
        {
            () => new("configuration", events),
            () => new("scene", events),
            () => new("presentation", events),
        };
        var host = new FakeActivationHost();

        var result = host.Activate(factories, failAt: 2);

        Assert.False(result.Success);
        Assert.NotNull(result.Detail);
        Assert.Equal(
            new[] { "dispose:scene", "dispose:configuration" },
            events);

        var scene = new SceneSession(new SelectionSession());
        Assert.Equal(SceneRefreshOutcome.Applied,
            scene.TryRefresh(Snapshot(1)).Outcome);
        var camera = new CameraDescriptor(
            new CameraId(Guid.NewGuid(), 0), "camera", CameraKind.Game,
            IsLive: true, IsDefault: true);
        Assert.Equal(SceneRefreshOutcome.Applied,
            scene.TryRefresh(Snapshot(2, camera)).Outcome);
        var rejected = scene.TryRefresh(Snapshot(
            3, camera,
            new CameraDescriptor(
                new CameraId(Guid.NewGuid(), 0), "second", CameraKind.Game,
                IsLive: true, IsDefault: true)));
        Assert.Equal(SceneRefreshOutcome.RejectedInvalidCandidate,
            rejected.Outcome);
        Assert.Equal(2UL, scene.Revision);
    }

    [Fact]
    public void Successful_activation_disposes_all_resources_in_reverse_order()
    {
        var events = new List<string>();
        var host = new FakeActivationHost();

        var result = host.Activate(new Func<FakeActivationResource>[]
        {
            () => new("configuration", events),
            () => new("scene", events),
            () => new("presentation", events),
        });
        host.Dispose();

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "dispose:presentation", "dispose:scene", "dispose:configuration" },
            events);
    }

    private static SceneSnapshot Snapshot(
        ulong revision,
        params CameraDescriptor[] cameras) =>
        new(
            revision,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            cameras,
            Array.Empty<PropDescriptor>());
}
