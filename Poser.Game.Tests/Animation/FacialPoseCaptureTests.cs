using System.Reflection;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain;
using Poser.Domain.Operations;
using Poser.Application.Selection;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Animation;
using Poser.Game.Bindings;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Animation;

public sealed class FacialPoseCaptureTests
{
    [Fact]
    public void Capture_pending_is_single_owner_and_cancelled_retry_can_apply()
    {
        using var app = new CaptureHarness();
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        var pending = app.Capture.LastReceipt;
        Assert.False(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        Assert.Same(pending, app.Capture.LastReceipt);
        Assert.Equal(OperationReceiptState.Cancelled, app.Capture.CancelPending()!.State);

        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        app.RunToApply();
        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);
        Assert.True(app.History.CanUndo);
        Assert.False(app.Capture.IsPending);
        app.Gestures.Dispose();
    }

    private sealed class CaptureHarness : IDisposable
    {
        private readonly PropertyProxy _boneProxy;

        public CaptureHarness()
        {
            Actor = new ActorId(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
            Skeleton = new SkeletonId(Actor, PoseSlot.Character, 4);
            Bone = new BoneId(Skeleton, 0, 7, "j_kao");
            Descriptor = Describe(Skeleton, Bone);

            Scene = new SceneSession(new SelectionSession());
            Scene.Refresh(Snapshot(Descriptor, 1));

            var actorProxy = DispatchProxy.Create<IActor, PropertyProxy>();
            var boneProxy = DispatchProxy.Create<IBone, PropertyProxy>();
            _boneProxy = (PropertyProxy)(object)boneProxy;
            _boneProxy.Values["LastRawTransform"] = Raw(5);
            // The bake asks the apply pass to keep this skeleton live; it
            // reaches the skeleton through the bone it is about to read.
            _boneProxy.Values["Skeleton"] =
                DispatchProxy.Create<ISkeleton, PropertyProxy>();
            Bindings = DispatchProxy.Create<IEntityBindings, BindingProxy>();
            var bindingProxy = (BindingProxy)(object)Bindings;
            bindingProxy.Actor = Actor;
            bindingProxy.LiveActor = actorProxy;
            bindingProxy.Bones = new Dictionary<BoneId, IBone> { [Bone] = boneProxy };

            Framework = FrameworkProxy.Create();
            Animation = AnimationPortProxy.Session();
            TransformRuntime = new TestTransformRuntime();
            TransformRuntime.Seed(Bone, 0);
            History = new TransformHistory();
            Gestures = new TransformGestureService(Scene, TransformRuntime, History);
            Transforms = new TransformCommandService(
                Scene, TransformRuntime, History, Gestures);
            SessionSource = new MutableSessionSource
            {
                Active = SessionGeneration.Create(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            };
            Posing = PosingProxy.Create();
            Capture = new FacialPoseCapture(
                Framework.Framework,
                Bindings,
                Scene,
                Animation,
                Transforms,
                Gestures,
                Posing.Service,
                SessionSource,
                DispatchProxy.Create<IPluginLog, PropertyProxy>());
        }

        /// <summary>Ticks a bake needs on a face that is holding still: two to
        /// reach caches a pass has refreshed for this skeleton, two stable
        /// readings to hand the facial drive back, and two more to prove the
        /// face is still holding still on the frame it will keep.</summary>
        public const int TicksToApply = 6;

        public void RunToApply()
        {
            for (var i = 0; i < TicksToApply; i++)
                Framework.FireUpdate();
        }

        public ActorId Actor { get; }
        public SkeletonId Skeleton { get; }
        public BoneId Bone { get; }
        public ActorDescriptor Descriptor { get; }
        public SceneSession Scene { get; }
        public IEntityBindings Bindings { get; }
        public FrameworkProxy Framework { get; }
        public AnimationSession Animation { get; }
        public TestTransformRuntime TransformRuntime { get; }
        public TransformHistory History { get; }
        public TransformGestureService Gestures { get; }
        public TransformCommandService Transforms { get; }
        public MutableSessionSource SessionSource { get; }
        public PosingProxy Posing { get; }
        public FacialPoseCapture Capture { get; }

        public void Dispose()
        {
            Capture.Dispose();
            Gestures.Dispose();
        }

        private static ActorDescriptor Describe(SkeletonId skeleton, BoneId bone) =>
            new(
                skeleton.Actor,
                "Actor",
                new[]
                {
                    new SkeletonDescriptor(
                        skeleton,
                        new[] { new BoneDescriptor(bone, bone.CanonicalName, null) }),
                });

        private static SceneSnapshot Snapshot(ActorDescriptor actor, ulong revision) =>
            new(
                revision,
                new[] { actor },
                Array.Empty<LightDescriptor>(),
                Array.Empty<CameraDescriptor>(),
                Array.Empty<PropDescriptor>());

        private static Poser.Domain.Transforms.Transform Raw(float x) =>
            new(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);
    }

    private sealed class MutableSessionSource : ISessionGenerationSource
    {
        public SessionGeneration? Active { get; set; }
        public SessionGeneration? ActiveSessionGeneration => Active;
    }

    private class FrameworkProxy : DispatchProxy
    {
        private Delegate? _update;

        public IFramework Framework { get; set; } = null!;

        public static FrameworkProxy Create()
        {
            var framework = DispatchProxy.Create<IFramework, FrameworkProxy>();
            var proxy = (FrameworkProxy)(object)framework;
            proxy.Framework = framework;
            return proxy;
        }

        public void FireUpdate() => _update?.DynamicInvoke(Framework);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_IsInFrameworkUpdateThread":
                    return true;
                case "add_Update":
                    _update = Delegate.Combine(_update, (Delegate)args![0]!);
                    return null;
                case "remove_Update":
                    _update = Delegate.Remove(_update, (Delegate)args![0]!);
                    return null;
                default:
                    return Default(method?.ReturnType);
            }
        }
    }

    private class AnimationPortProxy : DispatchProxy
    {
        public static AnimationSession Session() => new(
            DispatchProxy.Create<IAnimationTimelinePort, AnimationPortProxy>(),
            DispatchProxy.Create<IAnimationSpeedPort, AnimationPortProxy>(),
            DispatchProxy.Create<IAnimationStancePort, AnimationPortProxy>(),
            DispatchProxy.Create<IAnimationScrubPort, AnimationPortProxy>(),
            DispatchProxy.Create<IWorldRenderingRuntimePort, AnimationPortProxy>());

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_IsSupported":
                case "IsSupported":
                    return true;
                case "Blend":
                    args![3] = null;
                    return Outcome.Ok();
                case "get_SupportsForceLoop":
                case "get_SupportsStance":
                    return true;
                case "get_IsPhysicsFrozen":
                    return false;
                default:
                    if (method?.ReturnType == typeof(Outcome))
                        return Outcome.Ok();
                    return Default(method?.ReturnType);
            }
        }
    }

    private class PosingProxy : DispatchProxy
    {
        public Poser.Game.Services.IBonePosingService Service { get; set; } = null!;

        public static PosingProxy Create()
        {
            var service = DispatchProxy
                .Create<Poser.Game.Services.IBonePosingService, PosingProxy>();
            var proxy = (PosingProxy)(object)service;
            proxy.Service = service;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Default(method?.ReturnType);
    }

    private sealed class TestTransformRuntime : ITransformRuntimePort
    {
        private readonly Dictionary<TransformTargetId, TransformTargetState> _states = new();

        public void Seed(BoneId bone, float x)
        {
            var target = TransformTargetId.ForBone(bone);
            _states[target] = new TransformTargetState(
                target,
                PoseTransform.CreateChecked(
                    new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One),
                new BonePose(),
                false);
        }

        public TransformPortResult Capture(TransformTargetId target) =>
            _states.TryGetValue(target, out var state)
                ? TransformPortResult.Ok(state)
                : TransformPortResult.Fail(
                    TransformPortStatus.StaleTarget,
                    "missing test target");

        public TransformPortResult ApplyAbsolute(
            TransformTargetState baseline,
            PoseTransform desired,
            bool rawBaseline = false)
        {
            _states[baseline.Target] = baseline with { Transform = desired };
            return TransformPortResult.Ok();
        }

        public TransformPortResult Restore(TransformTargetState state)
        {
            _states[state.Target] = state;
            return TransformPortResult.Ok();
        }
    }

    private class PropertyProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name.StartsWith("get_", StringComparison.Ordinal) == true &&
                Values.TryGetValue(method.Name[4..], out var value))
                return value;
            return Default(method?.ReturnType);
        }
    }

    private class BindingProxy : DispatchProxy
    {
        public ActorId Actor;
        public IActor LiveActor = null!;
        public Dictionary<BoneId, IBone> Bones = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            args?[0] switch
            {
                ActorId id => new BindingResult<IActor>(
                    id == Actor ? BindingStatus.Success : BindingStatus.Missing,
                    id == Actor ? LiveActor : null),
                BoneId id when Bones.TryGetValue(id, out var bone) =>
                    new BindingResult<IBone>(BindingStatus.Success, bone),
                BoneId => new BindingResult<IBone>(BindingStatus.Missing),
                _ => Default(method?.ReturnType),
            };
    }

    private static object? Default(Type? type)
    {
        if (type == null || type == typeof(void))
            return null;
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        return null;
    }
}
