using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Cameras;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Tests.Cameras;

public sealed class CameraControlTests
{
    [Fact]
    public void Continuous_camera_edits_restore_the_start_and_final_values_once()
    {
        var f = new Fixture();
        for (int i = 1; i <= 3; i++)
        {
            f.Journal.BeginEdit("pan");
            Assert.True(f.Control.SetPan(f.Id, new(i, -i)).Success);
            f.Journal.EndEdit();
        }
        Assert.False(f.History.CanUndo);
        f.Control.Seal();
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(Vector2.Zero, f.Camera.Pan);
        f.History.CommitUndo(step);
        Assert.False(f.History.CanUndo);
        Assert.True(step.Redo());
        Assert.Equal(new Vector2(3, -3), f.Camera.Pan);
    }

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
    public void Lock_blocks_framing_but_allows_switching_back_to_main_camera()
    {
        var f = new Fixture();
        f.Camera.IsLocked = true;
        Assert.False(f.Control.SetPosition(f.Id, Vector3.One).Success);
        Assert.False(f.Control.SetPortrait(f.Id, true).Success);
        Assert.False(f.Control.ResetPosition(f.Id).Success);
        Assert.False(f.History.CanUndo);
        Assert.True(f.Control.SetLive(f.Id, true).Success);
        Assert.Same(f.Camera, f.Live);
        Assert.True(f.Control.SetLive(f.Id, false).Success);
        Assert.Same(f.Main, f.Live);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stale_generation_and_off_thread_commands_never_reach_camera(bool onThread)
    {
        var f = new Fixture();
        f.OnThread = onThread;
        f.CurrentId = new(f.Id.LogicalId, f.Id.Generation + 1);
        Assert.Null(f.Control.Read(f.Id));
        Assert.False(f.Control.SetPosition(f.Id, Vector3.One).Success);
        Assert.False(f.Control.SetLive(f.Id, true).Success);
        Assert.False(f.History.CanUndo);
        Assert.Equal(Vector3.Zero, f.Camera.Position);
        if (!onThread) Assert.Equal(0, f.BindingReads);
    }

    private sealed class Fixture
    {
        public readonly CameraId Id = new(Guid.NewGuid(), 0);
        public CameraId CurrentId;
        public bool OnThread = true;
        public int BindingReads;
        public readonly IVirtualCamera Camera = CameraStub(false);
        public readonly IVirtualCamera Main = CameraStub(true);
        public IVirtualCamera? Live;
        public readonly TransformHistory History = new();
        public readonly ValueJournal Journal;
        public readonly CameraControl Control;

        public Fixture()
        {
            CurrentId = Id;
            Live = Main;
            var bindings = Stub<IEntityBindings>((m, a) =>
            {
                BindingReads++;
                return m.Name switch
                {
                    "Resolve" => (CameraId)a![0]! == CurrentId
                        ? new BindingResult<IVirtualCamera>(BindingStatus.Success, Camera)
                        : new BindingResult<IVirtualCamera>(BindingStatus.StaleTarget),
                    "GetCameraId" => CurrentId,
                    _ => throw new InvalidOperationException(m.Name),
                };
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
            Control = new(bindings, Stub<IFramework>((_, _) => OnThread), cameras,
                new CameraSession(Journal, cameras, bindings));
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
