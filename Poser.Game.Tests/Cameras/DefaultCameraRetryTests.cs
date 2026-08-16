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
    public void Ready_native_entry_creates_the_default_live_camera()
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
    public void Pending_native_retry_recovers_once_and_then_stops_polling()
    {
        var gate = new NativeGate { Value = 0 };
        var setup = NewService(gate, isAvailable: true);
        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        setup.Framework.RaiseUpdate();
        Assert.Empty(setup.Service.Cameras);

        gate.Value = _nativeBlock;
        setup.Framework.RaiseUpdate();
        var main = Assert.Single(setup.Service.Cameras);
        var calls = gate.Calls;
        setup.Framework.RaiseUpdate();
        Assert.Same(main, setup.Service.LiveCamera);
        Assert.Equal(calls, gate.Calls);
    }

    [Fact]
    public void Unavailable_or_exited_capability_never_mints_a_camera()
    {
        var unavailable = NewService(new NativeGate { Value = _nativeBlock }, isAvailable: false);
        unavailable.GPose.IsGPosing = true;
        unavailable.Bus.Publish(new GPoseStateChangedEvent(true));
        unavailable.Framework.RaiseUpdate();
        Assert.False(unavailable.Service.IsAvailable);
        Assert.Empty(unavailable.Service.Cameras);

        var pending = NewService(new NativeGate { Value = 0 }, isAvailable: true);
        pending.GPose.IsGPosing = true;
        pending.Bus.Publish(new GPoseStateChangedEvent(true));
        pending.Framework.RaiseUpdate();
        pending.GPose.IsGPosing = false;
        pending.Bus.Publish(new GPoseStateChangedEvent(false));
        pending.Framework.RaiseUpdate();
        Assert.Empty(pending.Service.Cameras);
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
