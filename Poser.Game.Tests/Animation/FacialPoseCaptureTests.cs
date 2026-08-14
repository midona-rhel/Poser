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
    public void Capture_consumes_the_shared_session_source_and_exposes_receipt_lifecycle()
    {
        var constructor = Assert.Single(typeof(FacialPoseCapture).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(ISessionGenerationSource));
        Assert.NotNull(typeof(FacialPoseCapture).GetProperty("LastReceipt"));
        Assert.NotNull(typeof(FacialPoseCapture).GetMethod("CancelPending"));
        Assert.NotNull(typeof(FacialPoseCapture).GetEvent("ReceiptChanged"));
    }

    [Fact]
    public void Capture_does_not_own_or_mint_session_generation_state()
    {
        var sourceParameter = Assert.Single(
            typeof(FacialPoseCapture)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()),
            parameter =>
                parameter.ParameterType == typeof(ISessionGenerationSource));

        Assert.Equal("sessionGeneration", sourceParameter.Name);
        Assert.DoesNotContain(
            typeof(FacialPoseCapture).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            method => method.Name.Contains("Session", StringComparison.OrdinalIgnoreCase)
                && method.Name.Contains("New", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pending_receipt_is_non_terminal_and_terminal_states_are_explicit()
    {
        Assert.Contains(OperationReceiptState.Pending, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.Applied, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.RolledBack, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.Failed, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.RecoveryRequired, Enum.GetValues<OperationReceiptState>());
        Assert.Contains(OperationReceiptState.Cancelled, Enum.GetValues<OperationReceiptState>());
    }

    [Fact]
    public void Begin_without_an_active_session_has_no_operation_mutation_or_callback()
    {
        var scene = new SceneSession(new SelectionSession());
        var runtime = NewProxy<ITransformRuntimePort>();
        var gestures = new TransformGestureService(
            scene,
            runtime,
            new TransformHistory());
        var transforms = new TransformCommandService(
            scene,
            runtime,
            gestures.History,
            gestures);
        var source = new TestSessionSource();
        var capture = new FacialPoseCapture(
            NewProxy<IFramework>(),
            (StableBindingRegistry)RuntimeHelpers.GetUninitializedObject(
                typeof(StableBindingRegistry)),
            scene,
            new AnimationSession(NewProxy<IAnimationRuntimePort>()),
            transforms,
            gestures,
            NewProxy<Poser.Services.IBonePosingService>(),
            source,
            NewProxy<IPluginLog>());
        var callbacks = 0;
        capture.ReceiptChanged += _ => callbacks++;

        var actor = new ActorId(Guid.NewGuid(), 1);
        var result = capture.Begin(
            actor,
            new ActorDescriptor(actor, "Actor", Array.Empty<SkeletonDescriptor>()));

        Assert.False(result.Success);
        Assert.Null(capture.LastReceipt);
        Assert.False(capture.IsPending);
        Assert.False(capture.LastReceipt is { });
        Assert.Equal(0, callbacks);
        Assert.False(capture.LastReceipt is { State: OperationReceiptState.Pending });
        Assert.False(capture.IsPending);

        capture.Dispose();
        gestures.Dispose();
    }

    [Fact]
    public void Pending_begin_is_refused_until_cancel_and_fresh_retry()
    {
        using var app = new CaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.Capture.ReceiptChanged += receipts.Add;

        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        var pending = Assert.IsType<OperationReceipt>(app.Capture.LastReceipt);
        app.SetPreview(9);

        var refused = app.Capture.Begin(app.Actor, app.Descriptor);

        Assert.False(refused.Success);
        Assert.Contains("pending", refused.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(pending, app.Capture.LastReceipt);
        Assert.True(app.Capture.IsPending);
        Assert.Single(receipts);
        // A bake never pauses: freezing the actor would freeze the very
        // facial-layer output the delta is measured against.
        Assert.Equal(0, app.AnimationPort.PauseCount);

        var cancelled = app.Capture.CancelPending();
        Assert.Equal(OperationReceiptState.Cancelled, cancelled!.State);
        app.SetPreview(12);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        for (var i = 0; i < CaptureHarness.TicksToApply - 1; i++)
            app.Framework.FireUpdate();
        Assert.False(app.History.CanUndo);
        app.Framework.FireUpdate();

        var patch = Assert.IsType<TransformPatch>(app.History.PeekUndo());
        Assert.Single(patch.After);
        Assert.Equal(12, patch.After[0].Transform.Position.X);
        Assert.Single(app.TransformRuntime.RawBaselineWrites);
        Assert.True(app.TransformRuntime.RawBaselineWrites[0]);
    }

    [Fact]
    public void Existing_pending_recovery_refuses_begin_without_animation_or_receipt()
    {
        using var app = new CaptureHarness();
        app.TransformRuntime.FailApply = true;
        app.TransformRuntime.FailRestore = true;
        var failed = app.Transforms.SetAbsolute(
            TransformTargetId.ForBone(app.Bone),
            PoseTransform.CreateChecked(
                new Vector3(3, 0, 0),
                Quaternion.Identity,
                Vector3.One),
            "seed pending recovery");
        Assert.False(failed.Success);
        Assert.NotNull(app.Gestures.PendingRecovery);
        app.TransformRuntime.FailApply = false;
        app.TransformRuntime.FailRestore = false;

        var result = app.Capture.Begin(app.Actor, app.Descriptor);

        Assert.False(result.Success);
        Assert.Contains("recovery", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(app.Capture.LastReceipt);
        Assert.False(app.Capture.IsPending);
        Assert.Equal(0, app.AnimationPort.PauseCount);
        Assert.Equal(0, app.AnimationPort.ReleaseExpressionCallCount);
    }

    [Fact]
    public void Skeleton_replacement_cancels_without_touching_playback_speed()
    {
        using var app = new CaptureHarness();
        Assert.True(app.Animation.SetSpeed(app.Actor, 0.35f).Success);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        app.ReplaceSkeleton();

        app.RunToApply();

        Assert.Equal(OperationReceiptState.Cancelled, app.Capture.LastReceipt!.State);
        Assert.False(app.Capture.IsPending);
        // The user's own speed is the ONLY write on the actor: the bake owns
        // no speed to restore, so a cancellation has none to get wrong.
        Assert.Equal(new float?[] { 0.35f }, app.AnimationPort.OverallSpeedWrites);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Session_replacement_cancels_without_touching_playback_speed()
    {
        using var app = new CaptureHarness();
        Assert.True(app.Animation.SetSpeed(app.Actor, 0.45f).Success);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        app.SessionSource.Active = SessionGeneration.New();

        app.Framework.FireUpdate();

        Assert.Equal(OperationReceiptState.Cancelled, app.Capture.LastReceipt!.State);
        Assert.Equal(new float?[] { 0.45f }, app.AnimationPort.OverallSpeedWrites);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Off_thread_cancel_is_refused_without_invalidating_pending_token()
    {
        using var app = new CaptureHarness();
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        var pending = app.Capture.LastReceipt;
        app.Framework.IsFrameworkThread = false;

        var refused = app.Capture.CancelPending();

        Assert.Same(pending, refused);
        Assert.Same(pending, app.Capture.LastReceipt);
        Assert.True(app.Capture.IsPending);
        app.Framework.IsFrameworkThread = true;
        Assert.Equal(
            OperationReceiptState.Cancelled,
            app.Capture.CancelPending()!.State);
    }

    [Fact]
    public void Cancel_reentered_from_patch_observer_cannot_publish_two_terminals()
    {
        using var app = new CaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.Capture.ReceiptChanged += receipts.Add;
        app.History.PatchAppended += () => app.Capture.CancelPending();
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        app.RunToApply();

        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);
        Assert.Single(
            receipts,
            receipt => receipt.State != OperationReceiptState.Pending);
        Assert.True(app.History.CanUndo);
    }

    [Fact]
    public void Dispose_marks_owner_invalid_before_terminal_observer_reentry()
    {
        var app = new CaptureHarness();
        GestureResult? reentered = null;
        var terminals = 0;
        app.Capture.ReceiptChanged += receipt =>
        {
            if (receipt.State == OperationReceiptState.Pending)
                return;
            terminals++;
            reentered = app.Capture.Begin(app.Actor, app.Descriptor);
        };
        Assert.True(app.Animation.SetSpeed(app.Actor, 0.55f).Success);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        app.Capture.Dispose();

        Assert.NotNull(reentered);
        Assert.False(reentered.Value.Success);
        Assert.Contains(
            "disposed",
            reentered.Value.Detail!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, terminals);
        Assert.False(app.Capture.IsPending);
        Assert.Equal(0.55f, app.AnimationPort.OverallSpeedWrites[^1]);
        app.Gestures.Dispose();
    }

    [Fact]
    public void Dispose_reentered_from_patch_observer_publishes_one_terminal()
    {
        var app = new CaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.Capture.ReceiptChanged += receipts.Add;
        app.History.PatchAppended += app.Capture.Dispose;
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        app.RunToApply();

        Assert.Single(
            receipts,
            receipt => receipt.State != OperationReceiptState.Pending);
        Assert.True(app.History.CanUndo);
        Assert.False(app.Capture.IsPending);
        app.Gestures.Dispose();
    }

    [Fact]
    public void Off_thread_dispose_defers_invalidation_and_restore_to_framework_thread()
    {
        var app = new CaptureHarness();
        Assert.True(app.Animation.SetSpeed(app.Actor, 0.65f).Success);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        var pending = app.Capture.LastReceipt;
        app.Framework.IsFrameworkThread = false;

        app.Capture.Dispose();

        Assert.Same(pending, app.Capture.LastReceipt);
        Assert.True(app.Capture.IsPending);
        Assert.Equal(new float?[] { 0.65f }, app.AnimationPort.OverallSpeedWrites);
        Assert.False(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        app.Framework.IsFrameworkThread = true;
        app.Framework.RunQueued();

        Assert.False(app.Capture.IsPending);
        Assert.Equal(OperationReceiptState.Cancelled, app.Capture.LastReceipt!.State);
        Assert.Equal(new float?[] { 0.65f }, app.AnimationPort.OverallSpeedWrites);
        app.Gestures.Dispose();
    }

    [Fact]
    public void Dispose_during_apply_is_deferred_until_apply_and_terminal_publish_finish()
    {
        var app = new CaptureHarness();
        app.AddSecondFaceBone();
        var terminalCountDuringApply = -1;
        var suspendedDuringApply = false;
        var receipts = new List<OperationReceipt>();
        app.Capture.ReceiptChanged += receipts.Add;
        app.TransformRuntime.DuringApply = () =>
        {
            app.Capture.Dispose();
            terminalCountDuringApply = receipts.Count(receipt =>
                receipt.State != OperationReceiptState.Pending);
            suspendedDuringApply = app.Animation.CommandsSuspended;
        };
        Assert.True(app.Animation.SetSpeed(app.Actor, 0.75f).Success);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        app.RunToApply();

        Assert.Equal(0, terminalCountDuringApply);
        Assert.True(suspendedDuringApply);
        Assert.Equal(OperationReceiptState.Cancelled, app.Capture.LastReceipt!.State);
        Assert.Single(
            receipts,
            receipt => receipt.State != OperationReceiptState.Pending);
        Assert.Equal(0.75f, app.AnimationPort.OverallSpeedWrites[^1]);
        Assert.Single(app.TransformRuntime.RawBaselineWrites);
        Assert.False(app.History.CanUndo);
        Assert.False(app.Capture.IsPending);
        app.Gestures.Dispose();
    }

    [Fact]
    public void Dispose_during_apply_preserves_incomplete_transform_recovery_receipt()
    {
        var app = new CaptureHarness();
        app.AddSecondFaceBone();
        app.TransformRuntime.FailRestore = true;
        app.TransformRuntime.DuringApply = app.Capture.Dispose;
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        app.RunToApply();

        var receipt = Assert.IsType<OperationReceipt>(app.Capture.LastReceipt);
        Assert.Equal(OperationReceiptState.RecoveryRequired, receipt.State);
        Assert.False(Assert.IsType<TransformRecoveryReceipt>(receipt.Recovery).Complete);
        Assert.Same(receipt.Recovery, app.Gestures.PendingRecovery);
        Assert.Single(app.TransformRuntime.RawBaselineWrites);
        Assert.False(app.History.CanUndo);
        app.Gestures.Dispose();
    }

    // ── the bake the user presses ────────────────────────────────────────

    [Fact]
    public void Bake_under_pause_leaves_the_actor_paused_and_still_applies()
    {
        using var app = new CaptureHarness();
        // The user froze the actor first — the whole point of posing.
        Assert.True(app.Animation.Pause(app.Actor).Success);

        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        app.RunToApply();

        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);
        Assert.True(app.History.CanUndo);
        // The user's pause is the only speed write there has ever been: the
        // bake neither froze the actor nor handed it back at speed 1, which is
        // what used to make the face fall to the straight face the release
        // had queued.
        Assert.Equal(new float?[] { 0f }, app.AnimationPort.OverallSpeedWrites);
        Assert.True(app.Animation.IsPaused(app.Actor));
        Assert.False(app.Animation.CommandsSuspended);
    }

    [Fact]
    public void Bake_reads_the_face_from_a_refreshed_pass_not_from_the_button_press()
    {
        using var app = new CaptureHarness();
        // What the face showed when the button was pressed.
        app.SetPreview(3);

        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        // Only the apply pass writes the raw cache, and it has not run for a
        // skeleton nobody has posed. The value that arrives once the bake's
        // lease puts the skeleton back in the pass is the one that must be
        // baked.
        Assert.True(app.Posing.RawRefreshRequests > 0);
        app.SetPreview(21);
        app.RunToApply();

        var patch = Assert.IsType<TransformPatch>(app.History.PeekUndo());
        Assert.Equal(21, patch.After[0].Transform.Position.X);
        Assert.True(app.Posing.RawRefreshRequests >= CaptureHarness.TicksToApply);
    }

    [Fact]
    public void Settle_waits_for_the_face_to_stop_moving()
    {
        using var app = new CaptureHarness();
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        // Two ticks to reach a refreshed cache, then the capture tick.
        app.Framework.FireUpdate();
        app.Framework.FireUpdate();

        // The facial layer is still blending back; every tick reads something
        // different, and a bake that wrote now would diff against a
        // half-finished face.
        for (var i = 0; i < 6; i++)
        {
            app.SetPreview(30 + i);
            app.Framework.FireUpdate();
            Assert.False(app.History.CanUndo);
            Assert.True(app.Capture.IsPending);
        }

        // Held still for two readings: the layer has arrived.
        app.Framework.FireUpdate();
        app.Framework.FireUpdate();

        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);
        Assert.True(app.History.CanUndo);
    }

    [Fact]
    public void Settle_gives_up_waiting_on_a_face_that_never_stops_moving()
    {
        using var app = new CaptureHarness();
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);

        // A running idle animation blinks forever; the bake takes the frame it
        // is on rather than staying pending.
        for (var i = 0; i < 40 && app.Capture.IsPending; i++)
        {
            app.SetPreview(100 + i);
            app.Framework.FireUpdate();
        }

        Assert.False(app.Capture.IsPending);
        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);
        Assert.True(app.History.CanUndo);
    }

    [Fact]
    public void Bake_then_immediate_rebake_both_land_as_their_own_patches()
    {
        using var app = new CaptureHarness();
        app.SetPreview(4);
        Assert.True(app.Capture.Begin(app.Actor, app.Descriptor).Success);
        app.RunToApply();
        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);

        // Re-pick, press bake again straight away: no refusal, no leftover
        // suspension, no leftover pin.
        app.SetPreview(17);
        var second = app.Capture.Begin(app.Actor, app.Descriptor);

        Assert.True(second.Success);
        app.RunToApply();
        Assert.Equal(OperationReceiptState.Applied, app.Capture.LastReceipt!.State);
        var patch = Assert.IsType<TransformPatch>(app.History.PeekUndo());
        Assert.Equal(17, patch.After[0].Transform.Position.X);
        Assert.Equal(2, app.TransformRuntime.RawBaselineWrites.Count);
        Assert.False(app.Animation.CommandsSuspended);
        Assert.Empty(app.AnimationPort.OverallSpeedWrites);
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
        /// reach caches a pass has refreshed for this skeleton, one to seed the
        /// settle, and one more to prove the second stable reading.</summary>
        public const int TicksToApply = 4;

        public void RunToApply()
        {
            for (var i = 0; i < TicksToApply; i++)
                Framework.FireUpdate();
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
                case "ClearSlotSpeed":
                    if ((AnimationSlot)args![1]! == AnimationSlot.Facial)
                        ReleaseExpressionCallCount++;
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
