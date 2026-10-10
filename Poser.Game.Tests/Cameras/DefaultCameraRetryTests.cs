using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Game.Cameras;
using Poser.Services;
using System.Numerics;

namespace Poser.Game.Tests.Cameras;

public sealed unsafe class DefaultCameraRetryTests : IDisposable
{
    [Theory]
    [InlineData(MouseState.Right, false, true)]
    [InlineData(MouseState.Right, true, false)]
    public void Free_camera_looks_only_on_unlocked_right_drag(MouseState buttons, bool locked, bool looks)
    {
        var setup = NewService(new NativeGate(), true);
        using var service = setup.Service;
        var camera = new VirtualCamera(service, Poser.Domain.Scene.CameraKind.Free, false)
        {
            Position = new Vector3(1, 2, 3),
            Rotation = new Vector3(0.2f, 0.1f, 0),
            MouseSensitivity = 1f,
            IsLocked = locked,
        };
        var before = camera.Rotation;
        MouseFrame mouse = new() { ButtonsPressed = buttons, DeltaX = 12, DeltaY = 8 };
        service.HandleFreeCameraInput(camera, &mouse, null);
        service.UpdateFreeCamera(camera);
        Assert.Equal(new Vector3(1, 2, 3), camera.Position);
        Assert.Equal(looks, camera.Rotation != before);
        Assert.Equal(looks ? Vector2.Zero : new Vector2(12, 8), mouse.Delta);
        var after = camera.Rotation;
        service.UpdateFreeCamera(camera);
        Assert.Equal(after, camera.Rotation);
    }

    private readonly nint _nativeBlock;

    public DefaultCameraRetryTests()
    {
        _nativeBlock = Marshal.AllocHGlobal(sizeof(NativeCamera));
        new Span<byte>((void*)_nativeBlock, sizeof(NativeCamera)).Clear();
    }

    public void Dispose() => Marshal.FreeHGlobal(_nativeBlock);

    [Fact]
    public void Property_reset_is_one_undoable_edit_and_respects_lock()
    {
        var setup = NewService(new NativeGate { Value = _nativeBlock }, isAvailable: true);
        using var service = setup.Service;
        setup.GPose.IsGPosing = true;
        setup.Bus.Publish(new GPoseStateChangedEvent(true));
        var camera = service.CreateCamera(Poser.Domain.Scene.CameraKind.Game)!;
        var history = new Poser.Application.Transforms.TransformHistory();
        var id = new Poser.Domain.Identity.CameraId(Guid.NewGuid(), 0);
        var control = new CameraControl(new CameraBinding(camera, id), setup.Framework, service,
            new Poser.Application.Transforms.ValueJournal(history));
        camera.FoV = 0.4f;
        camera.Zoom = 7f;
        camera.FixedPosition = new Vector3(1, 2, 3);
        camera.TogglePortraitMode();
        var roll = camera.Roll;
        camera.IsLocked = true;
        Assert.False(control.ResetProperties(id).Success);
        Assert.False(history.CanUndo);
        camera.IsLocked = false;
        Assert.True(control.ResetProperties(id).Success);
        Assert.Null(camera.FixedPosition);
        Assert.False(camera.IsPortraitMode);
        var reset = Assert.IsType<Poser.Application.Transforms.JournalStep>(history.PeekUndo());
        Assert.True(reset.Undo());
        history.CommitUndo(reset);
        Assert.False(history.CanUndo);
        Assert.Equal(0.4f, camera.FoV);
        Assert.Equal(7f, camera.Zoom);
        Assert.Equal(new Vector3(1, 2, 3), camera.FixedPosition);
        Assert.True(camera.IsPortraitMode);
        Assert.Equal(roll, camera.Roll);
        Assert.Same(camera, service.LiveCamera);
        Assert.True(reset.Redo());
        Assert.Null(camera.FixedPosition);
        Assert.False(camera.IsPortraitMode);
        Assert.Equal(0f, camera.FoV);
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
        Assert.True(main.IsDefault);
        Assert.Equal("Main Camera", main.Name);
        var calls = gate.Calls;
        setup.Framework.RaiseUpdate();
        Assert.Same(main, setup.Service.LiveCamera);
        Assert.Equal(calls, gate.Calls);
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

    private static IEntityBindings CameraBinding(Poser.Entities.IVirtualCamera camera, Poser.Domain.Identity.CameraId id)
    {
        var bindings = DispatchProxy.Create<IEntityBindings, CameraBindingProxy>();
        ((CameraBindingProxy)(object)bindings).Bind(camera, id);
        return bindings;
    }

    public class CameraBindingProxy : DispatchProxy
    {
        private Poser.Entities.IVirtualCamera _camera = null!;
        private Poser.Domain.Identity.CameraId _id;

        public void Bind(Poser.Entities.IVirtualCamera camera, Poser.Domain.Identity.CameraId id) =>
            (_camera, _id) = (camera, id);

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name switch
            {
                "Resolve" when args![0] is Poser.Domain.Identity.CameraId => new BindingResult<Poser.Entities.IVirtualCamera>(BindingStatus.Success, _camera),
                "GetCameraId" => _id,
                _ => throw new InvalidOperationException(method.Name),
            };
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
