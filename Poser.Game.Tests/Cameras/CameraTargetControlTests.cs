using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Cameras;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Tests.Cameras;

public sealed class CameraTargetControlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stale_or_off_thread_camera_commands_do_not_touch_native_state(bool onThread)
    {
        var id = new CameraId(Guid.NewGuid(), 3);
        var bindings = Stub<IEntityBindings>((method, args) =>
        {
            Assert.True(onThread);
            Assert.Equal("Resolve", method);
            Assert.Equal(id, args![0]);
            return new BindingResult<IVirtualCamera>(BindingStatus.StaleTarget);
        });
        var control = new CameraTargetControl(bindings, Stub<IFramework>((_, _) => onThread),
            null!, null!, null!, null!);
        Assert.Null(control.Read(id));
        Assert.False(control.Follow(id, ActorId.New(), "Actor").Success);
        Assert.False(control.SetTargetLocked(id, true).Success);
        Assert.False(control.ToggleTrackedBone(id, default).Success);
        Assert.False(control.Recenter(id, null).Success);
    }

    [Fact]
    public void Follow_and_tracking_edits_use_existing_history_and_respect_camera_lock()
    {
        var f = new Fixture();
        Assert.True(f.Control.Follow(f.Id, f.ActorId, "Actor").Success);
        Assert.Equal(f.ActorId, f.Camera.TargetActorId);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.Null(f.Camera.TargetActorId);
        Assert.True(step.Redo());
        Assert.Equal(f.ActorId, f.Camera.TargetActorId);

        Assert.True(f.Control.SetTrackingMode(f.Id, CameraTrackingMode.Pan).Success);
        step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(CameraTrackingMode.None, f.Camera.TrackingMode);
        Assert.True(step.Redo());
        Assert.Equal(CameraTrackingMode.Pan, f.Camera.TrackingMode);
        f.Camera.IsLocked = true;
        Assert.False(f.Control.SetTracking(f.Id, true).Success);
        Assert.False(f.Control.ToggleTrackedBone(f.Id, f.BoneId).Success);
        Assert.Empty(f.Camera.TrackedBones);
        Assert.Same(step, f.History.PeekUndo());
    }

    [Fact]
    public void Reconciliation_drops_stale_bones_without_retargeting_a_new_actor_generation()
    {
        var f = new Fixture();
        Assert.True(f.Control.Follow(f.Id, f.ActorId, "Actor").Success);
        Assert.True(f.Control.ToggleTrackedBone(f.Id, f.BoneId).Success);
        var reading = f.Control.Read(f.Id)!;
        Assert.Single(reading.TrackedBones);
        f.CurrentActorId = f.ActorId.NextGeneration();
        Assert.False(f.Control.Reconcile(f.Id).Success);
        Assert.Null(f.Camera.TargetActorId);
        Assert.Empty(f.Camera.TrackedBones);
        Assert.Single(reading.TrackedBones); // A UI snapshot never aliases the native list.
        Assert.False(f.Control.ToggleTrackedBone(f.Id, f.BoneId).Success);
        Assert.False(f.Control.Follow(f.Id, f.ActorId, "Actor").Success);
        Assert.True(f.Control.Follow(f.Id, f.CurrentActorId, "Actor").Success);
    }

    private sealed class Fixture
    {
        public readonly CameraId Id = new(Guid.NewGuid(), 0);
        public readonly ActorId ActorId = ActorId.New();
        public ActorId CurrentActorId;
        public readonly BoneId BoneId;
        public readonly IVirtualCamera Camera;
        public readonly TransformHistory History = new();
        public readonly CameraTargetControl Control;

        public Fixture()
        {
            CurrentActorId = ActorId;
            BoneId = new(new(ActorId, PoseSlot.Character, 0), 0, 1, "head");
            var actor = Stub<IActor>((method, _) => method == "get_Name" ? "Actor" : throw new InvalidOperationException(method));
            var bone = Stub<IBone>((method, _) => throw new InvalidOperationException(method));
            var state = new Dictionary<string, object?>
            {
                ["IsValid"] = true, ["IsLocked"] = false, ["IsLive"] = false,
                ["IsTracking"] = false, ["IsTargetLocked"] = false,
                ["TargetActorId"] = null, ["TargetActor"] = null,
                ["TrackingMode"] = CameraTrackingMode.None, ["TrackedBones"] = new List<IBone>(),
            };
            Camera = Stub<IVirtualCamera>((method, args) =>
            {
                if (method.StartsWith("get_")) return state[method[4..]];
                if (method.StartsWith("set_")) { state[method[4..]] = args![0]; return null; }
                throw new InvalidOperationException(method);
            });
            var bindings = Stub<IEntityBindings>((method, args) => method switch
            {
                "GetCameraId" => Id,
                "GetActorId" => CurrentActorId,
                "GetBoneId" => BoneId,
                "Resolve" => args![0] switch
                {
                    CameraId id when id == Id => new BindingResult<IVirtualCamera>(BindingStatus.Success, Camera),
                    ActorId id => id == CurrentActorId ? new BindingResult<IActor>(BindingStatus.Success, actor) : new BindingResult<IActor>(BindingStatus.StaleTarget),
                    BoneId id => id.Skeleton.Actor == CurrentActorId ? new BindingResult<IBone>(BindingStatus.Success, bone) : new BindingResult<IBone>(BindingStatus.StaleTarget),
                    _ => throw new InvalidOperationException(),
                },
                _ => throw new InvalidOperationException(method),
            });
            var cameras = Stub<IVirtualCameraService>((method, args) =>
            {
                if (method == "get_IsAvailable") return true;
                if (method == "SetTargetActor")
                {
                    state["TargetActorId"] = args![2]; state["TargetActor"] = args[1]; return true;
                }
                if (method == "ClearTargetActor")
                {
                    state["TargetActorId"] = null; state["TargetActor"] = null;
                    state["IsTargetLocked"] = false; return null;
                }
                throw new InvalidOperationException(method);
            });
            Control = new(bindings, Stub<IFramework>((_, _) => true), cameras,
                Stub<IActorManager>((method, _) => method == "GetGPoseTarget" ? actor : throw new InvalidOperationException(method)),
                null!, new CameraSession(new ValueJournal(History), cameras, bindings));
        }
    }

    private static T Stub<T>(Func<string, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Call = call;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args);
    }
}
