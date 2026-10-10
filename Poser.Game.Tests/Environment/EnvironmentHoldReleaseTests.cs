using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Game.Environment;
using Poser.Services;
using Poser.Domain.Scene;
using Poser.Application.Events;
using CSEnvManager = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager;

namespace Poser.Game.Tests.Environment;

public sealed class EnvironmentHoldReleaseTests
{
    [Fact]
    public unsafe void Held_weather_is_supplied_to_each_native_update_without_restarting_transition()
    {
        const byte weather = 42;
        var factory = new TestFactory();
        using var service = Create(factory, ClientStateProxy.Create(out _));
        CSEnvManager manager = default;
        service.SetWeather(&manager, weather, 2f);
        for (int i = 0; i < 3; i++)
        {
            manager.ActiveWeather = 7; // A later game write, not the authored value.
            manager.TransitionTime = 1f;
            Assert.Equal((nint)123, factory.WeatherDetour!(&manager, 3f, 4f));
            Assert.Equal(weather, factory.WeatherHook.ObservedWeather);
            Assert.Equal(1f, manager.TransitionTime);
        }

        service.IsWeatherOverrideEnabled = false;
        manager.ActiveWeather = 8;
        factory.WeatherDetour!(&manager, 0f, 0f);
        Assert.Equal((byte)8, factory.WeatherHook.ObservedWeather);
        service.IsWeatherOverrideEnabled = true;
        factory.WeatherDetour!(&manager, 0f, 0f);
        manager.ActiveWeather = 9;
        factory.WeatherDetour!(&manager, 0f, 0f);
        Assert.Equal((byte)8, factory.WeatherHook.ObservedWeather);
    }

    [Fact]
    public void Territory_and_logout_release_all_successful_holds()
    {
        var factory = new TestFactory();
        var clientState = ClientStateProxy.Create(out var events);
        using var service = Create(factory, clientState);
        service.IsTimeFrozen = true;
        service.IsWeatherOverrideEnabled = true;
        service.SetSectionHeld(EnvSection.Sky, true);
        service.SetSectionHeld(EnvSection.Wind, true);

        events.RaiseTerritoryChanged(5);
        Assert.False(service.IsTimeFrozen);
        Assert.False(service.IsWeatherOverrideEnabled);
        Assert.False(service.IsSectionHeld(EnvSection.Sky));
        Assert.False(service.IsSectionHeld(EnvSection.Wind));

        service.IsTimeFrozen = true;
        service.SetSectionHeld(EnvSection.Fog, true);
        events.RaiseLogout();
        Assert.False(service.IsTimeFrozen);
        Assert.False(service.IsSectionHeld(EnvSection.Fog));
    }

    [Fact]
    public void Territory_change_invalidates_history_without_writing_the_new_room()
    {
        var housing = new TestHousingBrightness { State = new(0.3f, 0.4f, 0.8f) };
        var client = ClientStateProxy.Create(out var events);
        using var service = Create(new TestFactory(), client, housing: housing);
        ulong oldBinding = service.HousingInteriorBinding;
        Assert.True(service.TrySetInteriorBrightness(0.7f, oldBinding));

        housing.State = new(0.2f, 0.2f, 0.6f); // the newly resolved room
        events.RaiseTerritoryChanged(8);

        Assert.False(service.TrySetInteriorBrightness(0.9f, oldBinding));
        Assert.False(service.ReleaseInteriorBrightness(oldBinding));
        Assert.Equal([0.7f], housing.Writes);
        Assert.Equal(0.2f, service.InteriorBrightness);
    }

    private static EnvironmentService Create(
        TestFactory factory,
        IClientState clientState,
        IHousingBrightnessNative? housing = null)
    {
        return new EnvironmentService(
            clientState,
            NewProxy<ISigScanner>(),
            NewProxy<IGameInteropProvider>(),
            NewProxy<IDataManager>(),
            NewProxy<IPluginLog>(),
            new TestEventBus(),
            factory,
            housing);
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

    /// <summary>Captures event subscriptions off the IClientState proxy so the
    /// tests can raise TerritoryChanged/Logout exactly as Dalamud would.</summary>
    private sealed class ClientStateEvents
    {
        public Delegate? TerritoryChanged;
        public Delegate? Logout;

        public void RaiseTerritoryChanged(ushort territory) =>
            TerritoryChanged?.DynamicInvoke(Convert.ChangeType(
                territory,
                TerritoryChanged.GetType().GetMethod("Invoke")!
                    .GetParameters()[0].ParameterType));

        public void RaiseLogout()
        {
            if (Logout is null)
                return;
            var parameters = Logout.GetType().GetMethod("Invoke")!.GetParameters();
            Logout.DynamicInvoke(new object?[parameters.Length]);
        }
    }

    private class ClientStateProxy : DispatchProxy
    {
        private ClientStateEvents _events = null!;

        public static IClientState Create(out ClientStateEvents events)
        {
            var proxy = DispatchProxy.Create<IClientState, ClientStateProxy>();
            events = new ClientStateEvents();
            ((ClientStateProxy)(object)proxy)._events = events;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name ?? string.Empty;
            if (name.StartsWith("add_", StringComparison.Ordinal) && args is [Delegate handler])
            {
                if (name.Contains("TerritoryChanged"))
                    _events.TerritoryChanged = Delegate.Combine(_events.TerritoryChanged, handler);
                else if (name.Contains("Logout"))
                    _events.Logout = Delegate.Combine(_events.Logout, handler);
                return null;
            }
            if (name.StartsWith("remove_", StringComparison.Ordinal) && args is [Delegate removed])
            {
                if (name.Contains("TerritoryChanged"))
                    _events.TerritoryChanged = Delegate.Remove(_events.TerritoryChanged, removed);
                else if (name.Contains("Logout"))
                    _events.Logout = Delegate.Remove(_events.Logout, removed);
                return null;
            }
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }

    private class TestEnvHook : IEnvHook
    {
        public bool IsEnabled { get; private set; }
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
        public void Dispose() { }
    }

    private sealed class TestHousingBrightness : IHousingBrightnessNative
    {
        public HousingBrightnessState? State { get; set; }
        public List<float> Writes { get; } = [];
        public bool TryRead(out HousingBrightnessState state)
        {
            state = State.GetValueOrDefault();
            return State.HasValue;
        }
        public bool TryWrite(float target)
        {
            if (State is not { } state)
                return false;
            Writes.Add(target);
            State = state with { Target = target };
            return true;
        }
    }

    private sealed unsafe class TestWeatherHook : TestEnvHook, IEnvWeatherHook
    {
        public byte ObservedWeather { get; private set; }
        public nint Original(CSEnvManager* manager, float a2, float a3)
        {
            ObservedWeather = manager->ActiveWeather;
            return 123;
        }
    }

    private sealed unsafe class TestEnvCopyHook : IEnvStateCopyHook
    {
        private readonly TestEnvHook _inner = new();
        public bool IsEnabled => _inner.IsEnabled;
        public void Enable() => _inner.Enable();
        public void Disable() => _inner.Disable();
        public void Dispose() => _inner.Dispose();
        public nint Original(EnvStateNative* dest, EnvStateNative* src) => 0;
    }

    private sealed class TestFactory : IEnvironmentNativeFactory
    {
        public TestEnvHook TimeHook { get; } = new();
        public TestWeatherHook WeatherHook { get; } = new();
        public UpdateEnvironmentDelegate? WeatherDetour { get; private set; }
        public TestEnvCopyHook EnvCopyHook { get; } = new();

        public IEnvHook CreateTimeHook(
            ISigScanner scanner, IGameInteropProvider hooking, UpdateEorzeaTimeDelegate detour) =>
            TimeHook;

        public IEnvWeatherHook CreateWeatherHook(
            ISigScanner scanner, IGameInteropProvider hooking, UpdateEnvironmentDelegate detour)
        {
            WeatherDetour = detour;
            return WeatherHook;
        }

        public IEnvStateCopyHook CreateEnvStateCopyHook(
            ISigScanner scanner, IGameInteropProvider hooking, EnvStateCopyDelegate detour) =>
            EnvCopyHook;

        public IEnvStateCopyHook CreateEnvStateCopyCallSiteHook(
            ISigScanner scanner, IGameInteropProvider hooking, EnvStateCopyDelegate detour) =>
            EnvCopyHook;
    }

    private sealed class TestEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        public void Dispose() { }
        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
        }
        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }
        public void Publish<T>(T evt) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
            {
                foreach (var handler in list.ToArray())
                    ((Action<T>)handler)(evt);
            }
        }
    }
}
