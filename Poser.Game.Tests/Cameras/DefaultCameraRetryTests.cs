using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Game.Cameras;
using Poser.Services;
using Dalamud.Game.ClientState.Keys;
using System.Numerics;

namespace Poser.Game.Tests.Cameras;

public sealed unsafe class DefaultCameraRetryTests : IDisposable
{
    [Theory]
    [InlineData(VirtualKey.W, VirtualKey.A)]
    [InlineData(VirtualKey.A, VirtualKey.W)]
    public void Held_keys_move_together_even_when_native_input_was_consumed(VirtualKey first, VirtualKey second)
    {
        var keys = DispatchProxy.Create<IKeyState, HeldKeysProxy>();
        var held = ((HeldKeysProxy)(object)keys).Held;
        var setup = NewService(new NativeGate(), true, keys);
        using var service = setup.Service;
        var camera = new VirtualCamera(service, Poser.Domain.Scene.CameraKind.Free, false)
        { MovementSpeed = 1f };
        Vector3 Move()
        {
            KeyboardFrame consumed = default;
            var before = camera.Position;
            // The consumable buffer is empty; the held-key source retains
            // both keys, independently of native input consumption.
            service.HandleFreeCameraInput(camera, null, &consumed);
            service.UpdateFreeCamera(camera);
            return camera.Position - before;
        }
        held.Add(first);
        var initial = Move();
        held.Add(second);
        for (var frame = 0; frame < 5; frame++)
            Assert.Equal(new Vector3(-1, 0, -1), Move());
        held.Remove(second);
        Assert.Equal(initial, Move());
        held.Add(second);
        held.Add(VirtualKey.SHIFT);
        var fast = Move();
        Assert.Equal(-VirtualCameraService.CameraSettings.FastMultiplier, fast.X, 4);
        Assert.Equal(-VirtualCameraService.CameraSettings.FastMultiplier, fast.Z, 4);
        held.Remove(VirtualKey.SHIFT);
        held.Add(VirtualKey.CONTROL);
        var slow = Move();
        Assert.Equal(-VirtualCameraService.CameraSettings.SlowMultiplier, slow.X, 4);
        Assert.Equal(-VirtualCameraService.CameraSettings.SlowMultiplier, slow.Z, 4);
        held.Clear();
        Assert.Equal(Vector3.Zero, Move());
        held.Add(first);
        service.SuppressFlightKeys = true;
        Assert.Equal(Vector3.Zero, Move());
        service.SuppressFlightKeys = false;
        camera.IsLocked = true;
        Assert.Equal(Vector3.Zero, Move());
    }

    public class HeldKeysProxy : DispatchProxy
    {
        public readonly HashSet<VirtualKey> Held = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "get_Item" && args?[0] is VirtualKey key ? Held.Contains(key) : null;
    }

    private readonly nint _nativeBlock;

    public DefaultCameraRetryTests()
    {
        _nativeBlock = Marshal.AllocHGlobal(sizeof(NativeCamera));
        new Span<byte>((void*)_nativeBlock, sizeof(NativeCamera)).Clear();
    }

    public void Dispose() => Marshal.FreeHGlobal(_nativeBlock);
    [Fact]
    public void Copies_advance_the_clicked_name_series_without_filling_deleted_gaps()
    {
        var setup = NewService(new NativeGate { Value = _nativeBlock }, isAvailable: true);
        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        var one = setup.Service.CreateCamera(Poser.Domain.Scene.CameraKind.Game)!;
        one.Name = "Key 1";
        var two = setup.Service.CloneCamera(one)!;
        Assert.Equal("Key 2", two.Name);
        var three = setup.Service.CloneCamera(two)!;
        Assert.Equal("Key 3", three.Name);
        setup.Service.DestroyCamera(two);
        Assert.Equal("Key 4", setup.Service.CloneCamera(one)!.Name);
        Assert.Equal("Main Camera", setup.Service.Cameras.Single(x => x.IsDefault).Name);
    }

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

    private static Setup NewService(NativeGate gate, bool isAvailable, IKeyState? keys = null)
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
            isAvailable, keys);
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
