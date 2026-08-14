using System.Reflection;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Core;
using Poser.Entities;
using Poser.Game;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class GazeCapabilityTests
{
    [Fact]
    public void Missing_update_signature_keeps_gaze_registered_but_unavailable()
    {
        var factory = new TestNativeFactory
        {
            UpdateScan = () => throw new InvalidOperationException("missing"),
        };

        using var service = Create(factory);

        AssertUnavailable(service, "update signature");
        Assert.Equal(0, factory.HookCreateCount);
        Assert.Empty(factory.Hooks);
    }

    [Fact]
    public void Missing_loop_signature_does_not_create_a_hook()
    {
        var factory = new TestNativeFactory
        {
            LoopScan = () => throw new InvalidOperationException("missing"),
        };

        using var service = Create(factory);

        AssertUnavailable(service, "loop signature");
        Assert.Equal(0, factory.HookCreateCount);
    }

    [Fact]
    public void Hook_creation_failure_is_fail_soft()
    {
        var factory = new TestNativeFactory
        {
            CreateHook = (_, _) => throw new InvalidOperationException("create"),
        };

        using var service = Create(factory);

        AssertUnavailable(service, "hook creation");
        Assert.Equal(1, factory.HookCreateCount);
    }

    [Fact]
    public void Hook_enable_failure_disposes_the_partial_hook()
    {
        var hook = new TestHook { EnableFailure = true };
        var factory = new TestNativeFactory { Hook = hook };

        using var service = Create(factory);

        AssertUnavailable(service, "hook enable");
        Assert.Equal(1, hook.DisposeCount);
        Assert.Equal(0, factory.EventBusSubscriptions);
    }

    [Fact]
    public void Unavailable_service_refuses_all_mutations_without_events()
    {
        var factory = new TestNativeFactory
        {
            UpdateScan = () => throw new InvalidOperationException("missing"),
        };
        using var service = Create(factory);
        var actor = NewProxy<IActor>();

        service.SetGazeMode(actor, GazeTargetMode.Camera);
        service.SetGazeParts(actor, GazeTargetType.Eyes);
        service.SetGazeTarget(actor, actor);
        service.SetGazePosition(actor, new(1, 2, 3));
        service.SetPartPosition(actor, GazeTargetType.Eyes, new(4, 5, 6));
        service.SnapPartToCamera(actor, GazeTargetType.Eyes);
        service.SetPartLock(actor, GazeTargetType.Eyes, true);
        service.ResetGaze(actor);

        Assert.Equal(GazeTargetMode.None, service.GetGazeState(actor).Mode);
        Assert.Equal(GazeTargetType.All, service.GetGazeState(actor).TargetType);
        Assert.Equal((nint)0, service.GetGazeTargetAddress(actor));
        Assert.False(service.IsPartLocked(actor, GazeTargetType.Eyes));
        Assert.False(service.IsGazeEnabled(actor));
        Assert.Equal(0, factory.EventBus.PublishedCount);
    }

    [Fact]
    public void Successful_construction_preserves_registration_and_idempotent_dispose()
    {
        var hook = new TestHook();
        var factory = new TestNativeFactory { Hook = hook };
        var service = Create(factory);

        Assert.True(service.IsAvailable);
        Assert.Null(service.UnavailableDetail);
        Assert.Equal(1, hook.EnableCount);
        Assert.Equal(2, factory.EventBusSubscriptions);

        service.Dispose();
        service.Dispose();

        Assert.Equal(1, hook.DisposeCount);
        Assert.Equal(2, factory.EventBusUnsubscriptions);
    }

    private static GazeService Create(TestNativeFactory factory)
    {
        return new GazeService(
            NewProxy<IGPoseService>(),
            NewProxy<ICameraService>(),
            NewProxy<IObjectTable>(),
            factory.EventBus,
            NewProxy<ISigScanner>(),
            NewProxy<IGameInteropProvider>(),
            NewProxy<IPluginLog>(),
            factory);
    }

    private static void AssertUnavailable(GazeService service, string detail)
    {
        Assert.False(service.IsAvailable);
        Assert.NotNull(service.UnavailableDetail);
        Assert.Contains(detail, service.UnavailableDetail!, StringComparison.OrdinalIgnoreCase);
    }

    private static T NewProxy<T>() where T : class =>
        DispatchProxy.Create<T, DefaultProxy>();

    private class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }

    private sealed class TestNativeFactory : IGazeNativeFactory
    {
        public Func<nint>? UpdateScan { get; init; }
        public Func<nint>? LoopScan { get; init; }
        public Func<nint, GazeLoopDelegate, IGazeHook>? CreateHook { get; init; }
        public TestHook? Hook { get; init; }
        public TestEventBus EventBus { get; } = new();
        public List<IGazeHook> Hooks { get; } = new();
        public int HookCreateCount { get; private set; }
        public int EventBusSubscriptions => EventBus.SubscribedCount;
        public int EventBusUnsubscriptions => EventBus.UnsubscribedCount;

        public nint ScanUpdateLookAt(ISigScanner scanner) =>
            UpdateScan?.Invoke() ?? (nint)1;

        public nint ScanActorLookAtLoop(ISigScanner scanner) =>
            LoopScan?.Invoke() ?? (nint)2;

        public IGazeHook CreateActorLookAtHook(
            IGameInteropProvider hooks,
            nint address,
            GazeLoopDelegate detour)
        {
            HookCreateCount++;
            if (CreateHook is { } create)
                return create(address, detour);
            var hook = Hook ?? new TestHook();
            Hooks.Add(hook);
            return hook;
        }
    }

    private sealed class TestHook : IGazeHook
    {
        public bool EnableFailure { get; init; }
        public int EnableCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Enable()
        {
            EnableCount++;
            if (EnableFailure)
                throw new InvalidOperationException("enable");
        }

        public unsafe nint Original(ContainerInterface* args) => 0;

        public void Dispose() => DisposeCount++;
    }

    private sealed class TestEventBus : IEventBus
    {
        public int SubscribedCount { get; private set; }
        public int UnsubscribedCount { get; private set; }
        public int PublishedCount { get; private set; }

        public void Subscribe<T>(Action<T> handler) where T : IEvent => SubscribedCount++;
        public void Unsubscribe<T>(Action<T> handler) where T : IEvent => UnsubscribedCount++;
        public void Publish<T>(T evt) where T : IEvent => PublishedCount++;
        public void Dispose() { }
    }
}
