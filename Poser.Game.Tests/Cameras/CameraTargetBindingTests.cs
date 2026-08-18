using System.Reflection;
using System.Numerics;
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
    public void Same_generation_id_with_different_reference_is_stale()
    {
        var id = new ActorId(Guid.NewGuid(), 3);
        var current = ActorProxy();
        var retained = ActorProxy();
        var bindings = new Dictionary<ActorId, IActor> { [id] = current };

        Assert.False(StableBindingRegistry.IsCurrentCameraTarget(
            id, retained, bindings));
    }

    [Fact]
    public void Null_identity_with_pointer_only_residual_requires_clear()
    {
        Assert.True(StableBindingRegistry.HasCameraTargetResidual(
            ActorProxy(), string.Empty, Vector3.Zero));
    }

    [Fact]
    public void Exact_reference_for_admitted_generation_survives()
    {
        var id = new ActorId(Guid.NewGuid(), 4);
        var current = ActorProxy();
        var bindings = new Dictionary<ActorId, IActor> { [id] = current };

        Assert.True(StableBindingRegistry.IsCurrentCameraTarget(
            id, current, bindings));
    }

    private static IActor ActorProxy() =>
        DispatchProxy.Create<IActor, EmptyActorProxy>();

    private sealed class EmptyActorProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }
}
