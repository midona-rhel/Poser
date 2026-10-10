using System.Reflection;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Tests.Cameras;

/// <summary>Non-UI checks for binding admission's exact camera-follow
/// identity rules. These protect against stale native references surviving a
/// scene refresh or being rebound to a replacement actor.</summary>
public sealed class CameraTargetBindingTests
{
    [Fact]
    public void Retained_reference_is_current_only_if_it_is_the_exact_admitted_one()
    {
        var id = new ActorId(Guid.NewGuid(), 3);
        var current = ActorProxy();
        var bindings = new Dictionary<ActorId, IActor> { [id] = current };

        Assert.True(StableBindingRegistry.IsCurrentCameraTarget(id, current, bindings));
        Assert.False(StableBindingRegistry.IsCurrentCameraTarget(id, ActorProxy(), bindings));
    }

    private static IActor ActorProxy() =>
        DispatchProxy.Create<IActor, EmptyActorProxy>();

    private class EmptyActorProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }
}
