using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Game.Environment;
using Poser.Services;

namespace Poser.Game.Tests.Environment;

public sealed class EnvironmentHoldReleaseTests
{
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
    public void Transitions_without_holds_are_no_ops_and_release_failures_are_visible()
    {
        var emptyFactory = new TestFactory();
        var emptyState = ClientStateProxy.Create(out var emptyEvents);
        var emptyLog = new RecordingLog();
        using (var empty = Create(emptyFactory, emptyState, emptyLog))
        {
            emptyEvents.RaiseTerritoryChanged(5);
            emptyEvents.RaiseLogout();
            Assert.Empty(emptyLog.Errors);
            Assert.Equal(0, emptyFactory.TimeHook.DisableCount);
        }

        var factory = new TestFactory { WeatherHook = { DisableFailure = true } };
        var state = ClientStateProxy.Create(out var events);
        var log = new RecordingLog();
        using var service = Create(factory, state, log);
        service.IsTimeFrozen = true;
        service.IsWeatherOverrideEnabled = true;
        service.SetSectionHeld(EnvSection.Rain, true);
        events.RaiseTerritoryChanged(9);

        Assert.True(service.IsWeatherOverrideEnabled);
        Assert.False(service.IsTimeFrozen);
        Assert.False(service.IsSectionHeld(EnvSection.Rain));
        Assert.Single(log.Errors);
    }

    [Fact]
    public void Missing_native_signatures_keep_capabilities_unavailable_and_safe()
    {
        var factory = new TestFactory { ThrowOnTime = true, ThrowOnWeather = true };
        var state = ClientStateProxy.Create(out var events);
        using var service = Create(factory, state);
        Assert.False(service.IsTimeFreezeAvailable);
        Assert.False(service.IsWeatherOverrideAvailable);
        events.RaiseTerritoryChanged(1);
        events.RaiseLogout();
        Assert.False(service.IsTimeFrozen);
        Assert.False(service.IsWeatherOverrideEnabled);
    }
private static EnvironmentService Create(
        TestFactory factory,
        IClientState clientState,
        RecordingLog? log = null,
        TestEventBus? bus = null)
    {
        return new EnvironmentService(
            clientState,
            NewProxy<ISigScanner>(),
            NewProxy<IGameInteropProvider>(),
            NewProxy<IDataManager>(),
            (log ?? new RecordingLog()).Proxy(),
            bus ?? new TestEventBus(),
            factory);
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

    /// <summary>Records Error/Warning template strings off an IPluginLog proxy.</summary>
    private sealed class RecordingLog
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public IPluginLog Proxy()
        {
            var proxy = DispatchProxy.Create<IPluginLog, LogProxy>();
            ((LogProxy)(object)proxy).Owner = this;
            return proxy;
        }

        private class LogProxy : DispatchProxy
        {
            public RecordingLog Owner = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                var message = args?.FirstOrDefault(a => a is string) as string;
                if (message is not null)
                {
                    if (targetMethod?.Name == "Error")
                        Owner.Errors.Add(message);
                    else if (targetMethod?.Name == "Warning")
                        Owner.Warnings.Add(message);
                }
                return null;
            }
        }
    }

    private sealed class TestEnvHook : IEnvHook
    {
        public bool EnableFailure { get; set; }
        public bool DisableFailure { get; set; }
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool IsEnabled { get; private set; }

        public void Enable()
        {
            EnableCount++;
            if (EnableFailure)
                throw new InvalidOperationException("enable");
            IsEnabled = true;
        }

        public void Disable()
        {
            DisableCount++;
            if (DisableFailure)
                throw new InvalidOperationException("disable");
            IsEnabled = false;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed unsafe class TestEnvCopyHook : IEnvStateCopyHook
    {
        private readonly TestEnvHook _inner = new();
        public bool EnableFailure
        {
            get => _inner.EnableFailure;
            set => _inner.EnableFailure = value;
        }
        public int EnableCount => _inner.EnableCount;
        public bool IsEnabled => _inner.IsEnabled;
        public void Enable() => _inner.Enable();
        public void Disable() => _inner.Disable();
        public void Dispose() => _inner.Dispose();
        public nint Original(EnvStateNative* dest, EnvStateNative* src) => 0;
    }

    private sealed class TestFactory : IEnvironmentNativeFactory
    {
        public TestEnvHook TimeHook { get; } = new();
        public TestEnvHook WeatherHook { get; } = new();
        public TestEnvCopyHook EnvCopyHook { get; } = new();
        public bool ThrowOnTime { get; init; }
        public bool ThrowOnWeather { get; init; }
        public bool ThrowOnEnvCopy { get; init; }
        public bool ThrowOnEnvCopyCallSite { get; init; }

        public IEnvHook CreateTimeHook(
            ISigScanner scanner, IGameInteropProvider hooking, UpdateEorzeaTimeDelegate detour) =>
            ThrowOnTime ? throw new InvalidOperationException("time sig") : TimeHook;

        public IEnvHook CreateWeatherHook(
            ISigScanner scanner, IGameInteropProvider hooking, UpdateTerritoryWeatherDelegate detour) =>
            ThrowOnWeather ? throw new InvalidOperationException("weather sig") : WeatherHook;

        public IEnvStateCopyHook CreateEnvStateCopyHook(
            ISigScanner scanner, IGameInteropProvider hooking, EnvStateCopyDelegate detour) =>
            ThrowOnEnvCopy ? throw new InvalidOperationException("copy sig") : EnvCopyHook;

        public IEnvStateCopyHook CreateEnvStateCopyCallSiteHook(
            ISigScanner scanner, IGameInteropProvider hooking, EnvStateCopyDelegate detour) =>
            ThrowOnEnvCopyCallSite ? throw new InvalidOperationException("copy call site sig") : EnvCopyHook;
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
