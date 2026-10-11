using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Cameras;
using Poser.Application.Presentation;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Cameras;

public sealed class CameraControlTests
{
    [Fact]
    public void Portrait_undo_restores_both_the_mode_and_the_authored_roll()
    {
        var f = new Fixture();
        f.Camera.Roll = .4f;
        Assert.True(f.Control.SetPortrait(f.Id, true).Success);
        Assert.True(f.Camera.IsPortraitMode);
        Assert.Equal(.4f + MathF.PI / 2, f.Camera.Roll);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.False(f.Camera.IsPortraitMode);
        Assert.Equal(.4f, f.Camera.Roll);
        Assert.True(step.Redo());
        Assert.True(f.Camera.IsPortraitMode);
        Assert.Equal(.4f + MathF.PI / 2, f.Camera.Roll);
    }

    [Fact]
    public void A_locked_camera_refuses_and_journals_nothing_but_still_switches_live()
    {
        var f = new Fixture();
        f.Camera.IsLocked = true;
        var refused = f.Control.Set(f.Id, CameraProperties.Position, Vector3.One);
        Assert.Equal((false, "Unlock the camera first."), (refused.Success, refused.Detail));
        Assert.False(f.Control.SetPortrait(f.Id, true).Success);
        Assert.False(f.Control.ResetPosition(f.Id).Success);
        Assert.Equal(Vector3.Zero, f.Camera.Position);
        Assert.False(f.History.CanUndo);
        Assert.True(f.Control.SetLive(f.Id, true).Success);
        Assert.Same(f.Camera, f.Live);
        Assert.True(f.Control.SetLive(f.Id, false).Success);
        Assert.Same(f.Main, f.Live);
    }

    private sealed class Fixture
    {
        public readonly CameraId Id = new(Guid.NewGuid(), 0);
        public CameraId CurrentId;
        public readonly IVirtualCamera Camera = CameraStub(false);
        public readonly IVirtualCamera Main = CameraStub(true);
        public IVirtualCamera? Live;
        public readonly EditHistory History = new();
        public readonly ValueJournal Journal;
        public readonly CameraControl Control;

        public Fixture()
        {
            CurrentId = Id;
            Live = Main;
            var bindings = Stub<IEntityBindings>((m, a) =>
                m.Name switch
                {
                    "Resolve" => (CameraId)a![0]! == CurrentId
                        ? new BindingResult<IVirtualCamera>(BindingStatus.Success, Camera)
                        : new BindingResult<IVirtualCamera>(BindingStatus.StaleTarget),
                    "GetCameraId" => CurrentId,
                    _ => throw new InvalidOperationException(m.Name),
                });
            var cameras = Stub<IVirtualCameraService>((m, a) =>
            {
                if (m.Name == "get_IsAvailable") return true;
                if (m.Name == "get_Cameras") return new[] { Main, Camera };
                if (m.Name == "get_LiveCamera") return Live;
                if (m.Name == "SetLive") { Live = (IVirtualCamera)a![0]!; return null; }
                throw new InvalidOperationException(m.Name);
            });
            Journal = new(History);
            Control = new(bindings, Stub<IFramework>((_, _) => true), cameras, Journal);
        }
    }

    private static IVirtualCamera CameraStub(bool main)
    {
        var state = new Dictionary<string, object?> { ["IsValid"] = true, ["IsDefault"] = main };
        return Stub<IVirtualCamera>((m, a) =>
        {
            if (m.Name.StartsWith("set_")) { state[m.Name[4..]] = a![0]; return null; }
            if (m.Name == "TogglePortraitMode")
            {
                bool portrait = !(state.TryGetValue("IsPortraitMode", out var p) && (bool)p!);
                state["IsPortraitMode"] = portrait;
                state["Roll"] = (state.TryGetValue("Roll", out var r) ? (float)r! : 0f) +
                    (portrait ? MathF.PI / 2 : -MathF.PI / 2);
                return null;
            }
            if (m.Name.StartsWith("get_"))
                return state.TryGetValue(m.Name[4..], out var value) ? value :
                    m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
            throw new InvalidOperationException(m.Name);
        });
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Call = call;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
