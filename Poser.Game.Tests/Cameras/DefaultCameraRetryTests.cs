using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Game.Cameras;
using Poser.Services;

namespace Poser.Game.Tests.Cameras;

public sealed unsafe class DefaultCameraRetryTests : IDisposable
{
    private readonly nint _nativeBlock;

    public DefaultCameraRetryTests()
    {
        _nativeBlock = Marshal.AllocHGlobal(sizeof(NativeCamera));
        new Span<byte>((void*)_nativeBlock, sizeof(NativeCamera)).Clear();
    }

    public void Dispose() => Marshal.FreeHGlobal(_nativeBlock);

    [Fact]
    public void Native_ready_at_entry_mints_the_default_camera_immediately()
    {
        var gate = new NativeGate { Value = _nativeBlock };
        var setup = NewService(gate, isAvailable: true);

        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));

        var main = Assert.Single(setup.Service.Cameras);
        Assert.True(main.IsDefault);
        Assert.Equal("Main Camera", main.Name);
        Assert.Same(main, setup.Service.LiveCamera);
        Assert.Equal(1, setup.Bus.CameraListChanges);
    }

    [Fact]
    public void Missing_native_manager_at_entry_recovers_on_a_later_tick()
    {
        var gate = new NativeGate { Value = 0 };
        var setup = NewService(gate, isAvailable: true);

        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        Assert.Empty(setup.Service.Cameras);

        // Still not up: the retry keeps waiting without minting anything.
        setup.Framework.RaiseUpdate();
        setup.Framework.RaiseUpdate();
        Assert.Empty(setup.Service.Cameras);

        gate.Value = _nativeBlock;
        setup.Framework.RaiseUpdate();

        var main = Assert.Single(setup.Service.Cameras);
        Assert.True(main.IsDefault);
        Assert.Same(main, setup.Service.LiveCamera);

        // Recovery is terminal: the gate is no longer consulted per tick.
        var consulted = gate.Calls;
        setup.Framework.RaiseUpdate();
        setup.Framework.RaiseUpdate();
        Assert.Equal(consulted, gate.Calls);
        Assert.Single(setup.Service.Cameras);
    }

    [Fact]
    public void Never_available_capability_stays_truthfully_unavailable()
    {
        var gate = new NativeGate { Value = _nativeBlock };
        var setup = NewService(gate, isAvailable: false);

        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        setup.Framework.RaiseUpdate();
        setup.Framework.RaiseUpdate();

        Assert.False(setup.Service.IsAvailable);
        Assert.Empty(setup.Service.Cameras);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, setup.Bus.CameraListChanges);
    }

    [Fact]
    public void Gpose_exit_while_pending_cancels_the_retry()
    {
        var gate = new NativeGate { Value = 0 };
        var setup = NewService(gate, isAvailable: true);

        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        setup.Framework.RaiseUpdate();

        setup.GPose.IsGPosing = false;
        setup.Bus.Publish(new GPoseStateChangedEvent(false));

        gate.Value = _nativeBlock;
        var consulted = gate.Calls;
        setup.Framework.RaiseUpdate();
        setup.Framework.RaiseUpdate();

        Assert.Empty(setup.Service.Cameras);
        Assert.Equal(consulted, gate.Calls);
    }

    [Fact]
    public void Gpose_exit_after_recovery_tears_the_cameras_down_as_before()
    {
        var gate = new NativeGate { Value = 0 };
        var setup = NewService(gate, isAvailable: true);

        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        gate.Value = _nativeBlock;
        setup.Framework.RaiseUpdate();
        var main = Assert.Single(setup.Service.Cameras);

        setup.GPose.IsGPosing = false;
        setup.Bus.Publish(new GPoseStateChangedEvent(false));

        Assert.Empty(setup.Service.Cameras);
        Assert.Null(setup.Service.LiveCamera);
        Assert.False(main.IsValid);
        Assert.False(main.IsLive);
    }

    private sealed record Setup(
        VirtualCameraService Service,
        FakeFramework Framework,
        FakeGPoseService GPose,
        FakeEventBus Bus);

    private static Setup NewService(NativeGate gate, bool isAvailable)
    {
        var framework = new FakeFramework();
        var gPose = new FakeGPoseService();
        var bus = new FakeEventBus();
        var service = new VirtualCameraService(
            framework,
            NewProxy<IPluginLog>(),
            gPose,
            bus,
            gate.Read,
            isAvailable);
        return new Setup(service, framework, gPose, bus);
    }

    private sealed class NativeGate
    {
        public nint Value { get; set; }
        public int Calls { get; private set; }

        public nint Read()
        {
            Calls++;
            return Value;
        }
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

    private sealed class FakeGPoseService : IGPoseService
    {
        public bool IsGPosing { get; set; }
        public void Dispose() { }
        public void ExitForUnload() { }
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        public int CameraListChanges { get; private set; }

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
            if (evt is CameraListChangedEvent)
                CameraListChanges++;
            if (_handlers.TryGetValue(typeof(T), out var list))
            {
                foreach (var handler in list.ToArray())
                    ((Action<T>)handler)(evt);
            }
        }
    }

    private sealed class FakeFramework : IFramework
    {
        public event IFramework.OnUpdateDelegate? Update;
        public void RaiseUpdate() => Update?.Invoke(this);

        public DateTime LastUpdate => DateTime.MinValue;
        public DateTime LastUpdateUTC => DateTime.MinValue;
        public TimeSpan UpdateDelta => TimeSpan.Zero;
        public bool IsInFrameworkUpdateThread => true;
        public bool IsFrameworkUnloading => false;
        public System.Threading.Tasks.TaskFactory GetTaskFactory() =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task DelayTicks(long numTicks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task Run(Action action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task Run(Func<System.Threading.Tasks.Task> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task<T> Run<T>(Func<System.Threading.Tasks.Task<T>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task RunOnFrameworkThread(Action action) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task<T> RunOnFrameworkThread<T>(Func<T> func) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task RunOnFrameworkThread(Func<System.Threading.Tasks.Task> func) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task<T> RunOnFrameworkThread<T>(Func<System.Threading.Tasks.Task<T>> func) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task RunOnTick(Func<System.Threading.Tasks.Task> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public System.Threading.Tasks.Task<T> RunOnTick<T>(Func<System.Threading.Tasks.Task<T>> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Dalamud.Utility.IDebouncer CreateDebouncer(TimeSpan interval, Action action) =>
            throw new NotSupportedException();
    }
}
