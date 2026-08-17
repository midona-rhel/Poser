using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Selection;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Animation;
using Poser.Game.Bindings;

namespace Poser.Game.Tests.Animation;

public sealed class FacialPoseCaptureTests
{
[Fact]
    public void Capture_exposes_shared_session_receipts_and_refuses_without_a_session()
    {
        var constructor = Assert.Single(typeof(FacialPoseCapture).GetConstructors());
        Assert.Contains(constructor.GetParameters(), p => p.ParameterType == typeof(ISessionGenerationSource));
        Assert.NotNull(typeof(FacialPoseCapture).GetEvent("ReceiptChanged"));

        var scene = new SceneSession(new SelectionSession());
        var gestures = new TransformGestureService(scene, NewProxy<ITransformRuntimePort>(), new TransformHistory());
        var transforms = new TransformCommandService(scene, NewProxy<ITransformRuntimePort>(), gestures.History, gestures);
        var source = new TestSessionSource();
        using var capture = new FacialPoseCapture(NewProxy<IFramework>(),
            (StableBindingRegistry)RuntimeHelpers.GetUninitializedObject(typeof(StableBindingRegistry)),
            scene, new AnimationSession(NewProxy<IAnimationRuntimePort>()), transforms,
            gestures, NewProxy<Poser.Services.IBonePosingService>(), source, NewProxy<IPluginLog>());
        var actor = new ActorId(Guid.NewGuid(), 1);
        Assert.False(capture.Begin(actor, new ActorDescriptor(actor, "Actor", Array.Empty<SkeletonDescriptor>())).Success);
        Assert.Null(capture.LastReceipt);
        gestures.Dispose();
    }

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
private static T NewProxy<T>() where T : class =>
        DispatchProxy.Create<T, DefaultProxy>();

    private class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod?.Name == "get_IsInFrameworkUpdateThread")
                return true;
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }

    private sealed class TestSessionSource : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration => null;
    }

    private sealed class CaptureHarness : IDisposable
    {
        private readonly PropertyProxy _boneProxy;
        private readonly Dictionary<BoneId, IBone> _boneBindings;
        private ulong _sceneRevision = 1;

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
            Bindings = (StableBindingRegistry)RuntimeHelpers.GetUninitializedObject(
                typeof(StableBindingRegistry));
            SetField(Bindings, "_actorBindings", new Dictionary<ActorId, IActor>
            {
                [Actor] = actorProxy,
            });
            _boneBindings = new Dictionary<BoneId, IBone>
            {
                [Bone] = boneProxy,
            };
            SetField(Bindings, "_boneBindings", _boneBindings);

            Framework = FrameworkProxy.Create();
            AnimationPort = AnimationPortProxy.Create();
            Animation = new AnimationSession(AnimationPort.Port);
            TransformRuntime = new TestTransformRuntime();
            TransformRuntime.Seed(Bone, 0);
            TransformRuntime.LiveRaw = () =>
                ((Poser.Transform)_boneProxy.Values["LastRawTransform"]!)
                    .Position.X;
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
                NewProxy<IPluginLog>());
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

        /// <summary>
        /// The game, as far as a PAUSED actor's face is concerned: the facial
        /// layer only moves toward the restored timeline on the frames
        /// something is driving it. Everything else about the actor stays
        /// exactly where the user froze it.
        /// </summary>
        public void RunToApplyFrozen(float releasedFace)
        {
            for (var i = 0; i < TicksToApply + 2; i++)
            {
                if (AnimationPort.FacialSlotSpeed is > 0f)
                    SetPreview(releasedFace);
                Framework.FireUpdate();
            }
        }

        public ActorId Actor { get; }
        public SkeletonId Skeleton { get; }
        public BoneId Bone { get; }
        public ActorDescriptor Descriptor { get; private set; }
        public SceneSession Scene { get; }
        public StableBindingRegistry Bindings { get; }
        public FrameworkProxy Framework { get; }
        public AnimationPortProxy AnimationPort { get; }
        public AnimationSession Animation { get; }
        public TestTransformRuntime TransformRuntime { get; }
        public TransformHistory History { get; }
        public TransformGestureService Gestures { get; }
        public TransformCommandService Transforms { get; }
        public MutableSessionSource SessionSource { get; }
        public PosingProxy Posing { get; }
        public FacialPoseCapture Capture { get; }

        public void SetPreview(float x) =>
            _boneProxy.Values["LastRawTransform"] = Raw(x);

        public void AddSecondFaceBone()
        {
            var second = new BoneId(Skeleton, 0, 8, "j_f_mayu_l");
            var secondProxy = DispatchProxy.Create<IBone, PropertyProxy>();
            ((PropertyProxy)(object)secondProxy).Values["LastRawTransform"] = Raw(6);
            _boneBindings[second] = secondProxy;
            TransformRuntime.Seed(second, 0);
            Descriptor = new ActorDescriptor(
                Actor,
                "Actor",
                new[]
                {
                    new SkeletonDescriptor(
                        Skeleton,
                        new[]
                        {
                            new BoneDescriptor(Bone, Bone.CanonicalName, null),
                            new BoneDescriptor(second, second.CanonicalName, null),
                        }),
                });
            Scene.Refresh(Snapshot(Descriptor, ++_sceneRevision));
        }

        public void ReplaceSkeleton()
        {
            var replacement = new SkeletonId(Actor, PoseSlot.Character, 5);
            var replacementBone = new BoneId(replacement, 0, 7, "j_kao");
            Descriptor = Describe(replacement, replacementBone);
            Scene.Refresh(Snapshot(Descriptor, ++_sceneRevision));
        }

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

        private static Poser.Transform Raw(float x) =>
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
        private readonly Queue<Action> _queued = new();

        public IFramework Framework { get; set; } = null!;
        public bool IsFrameworkThread { get; set; } = true;

        public static FrameworkProxy Create()
        {
            var framework = DispatchProxy.Create<IFramework, FrameworkProxy>();
            var proxy = (FrameworkProxy)(object)framework;
            proxy.Framework = framework;
            return proxy;
        }

        public void FireUpdate() => _update?.DynamicInvoke(Framework);

        public void RunQueued()
        {
            while (_queued.Count > 0)
                _queued.Dequeue()();
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_IsInFrameworkUpdateThread":
                    return IsFrameworkThread;
                case "add_Update":
                    _update = Delegate.Combine(_update, (Delegate)args![0]!);
                    return null;
                case "remove_Update":
                    _update = Delegate.Remove(_update, (Delegate)args![0]!);
                    return null;
                case "RunOnFrameworkThread":
                    _queued.Enqueue((Action)args![0]!);
                    return Task.CompletedTask;
                default:
                    return Default(method?.ReturnType);
            }
        }
    }

    private class AnimationPortProxy : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; set; } = null!;
        public List<float?> OverallSpeedWrites { get; } = new();
        public int PauseCount => OverallSpeedWrites.Count(value => value == 0f);
        public int ReleaseExpressionCallCount { get; private set; }

        /// <summary>The facial layer's owned speed, or null when Poser owns
        /// none — the game's own value (0 on a paused actor) then applies.
        /// </summary>
        public float? FacialSlotSpeed { get; private set; }

        /// <summary>Refuses the slot-speed write, as the port does when the
        /// game's slot-speed hook is not active.</summary>
        public bool FailSetSlotSpeed { get; set; }

        public static AnimationPortProxy Create()
        {
            var port = DispatchProxy.Create<IAnimationRuntimePort, AnimationPortProxy>();
            var proxy = (AnimationPortProxy)(object)port;
            proxy.Port = port;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_IsSupported":
                case "IsSupported":
                    return true;
                case "SetOverallSpeed":
                    OverallSpeedWrites.Add((float)args![1]!);
                    return AnimationPortResult.Ok();
                case "ClearOverallSpeed":
                    OverallSpeedWrites.Add(null);
                    return AnimationPortResult.Ok();
                case "SetSlotSpeed":
                    if (FailSetSlotSpeed)
                        return AnimationPortResult.Fail("slot speed unavailable");
                    if ((AnimationSlot)args![1]! == AnimationSlot.Facial)
                        FacialSlotSpeed = (float)args[2]!;
                    return AnimationPortResult.Ok();
                case "ClearSlotSpeed":
                    if ((AnimationSlot)args![1]! == AnimationSlot.Facial)
                    {
                        ReleaseExpressionCallCount++;
                        FacialSlotSpeed = null;
                    }
                    return AnimationPortResult.Ok();
                case "Blend":
                    args![3] = null;
                    return AnimationPortResult.Ok();
                case "get_SupportsForceLoop":
                case "get_SupportsStance":
                    return true;
                case "get_IsPhysicsFrozen":
                    return false;
                default:
                    if (method?.ReturnType == typeof(AnimationPortResult))
                        return AnimationPortResult.Ok();
                    return Default(method?.ReturnType);
            }
        }
    }

    /// <summary>Counts the apply-pass leases the bake takes: nothing else
    /// refreshes the raw caches it reads.</summary>
    private class PosingProxy : DispatchProxy
    {
        public Poser.Services.IBonePosingService Service { get; set; } = null!;
        public int RawRefreshRequests { get; private set; }

        public static PosingProxy Create()
        {
            var service = DispatchProxy
                .Create<Poser.Services.IBonePosingService, PosingProxy>();
            var proxy = (PosingProxy)(object)service;
            proxy.Service = service;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "RequestRawTransformRefresh")
                RawRefreshRequests++;
            return Default(method?.ReturnType);
        }
    }

    private sealed class TestTransformRuntime : ITransformRuntimePort
    {
        private readonly Dictionary<TransformTargetId, TransformTargetState> _states = new();

        public bool FailApply { get; set; }
        public bool FailRestore { get; set; }
        public Action? DuringApply { get; set; }
        public List<bool> RawBaselineWrites { get; } = new();

        /// <summary>The live raw the REAL port diffs a rawBaseline write
        /// against (TransformRuntimePort.ApplyAbsolute reads
        /// bone.LastRawTransform at apply time). Recorded beside the desired
        /// value so a test can see whether the stored delta is identity.
        /// </summary>
        public Func<float>? LiveRaw { get; set; }
        public List<(float Desired, float Basis)> Writes { get; } = new();

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
            RawBaselineWrites.Add(rawBaseline);
            if (LiveRaw is { } live)
                Writes.Add((desired.Position.X, live()));
            DuringApply?.Invoke();
            if (FailApply)
                return TransformPortResult.Fail(
                    TransformPortStatus.Rejected,
                    "test apply failure");
            _states[baseline.Target] = baseline with { Transform = desired };
            return TransformPortResult.Ok();
        }

        public TransformPortResult Restore(TransformTargetState state)
        {
            if (FailRestore)
                return TransformPortResult.Fail(
                    TransformPortStatus.NativeUnavailable,
                    "test restore failure");
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

    private static void SetField(object target, string name, object value) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static object? Default(Type? type)
    {
        if (type == null || type == typeof(void))
            return null;
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        return null;
    }
}
