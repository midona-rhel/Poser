using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Animation;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Tests.Animation;

/// <summary>
/// Held expression ownership at the session/runtime boundary. Both UI surfaces
/// share the first restore point, frozen speed, and retryable release.
/// </summary>
public sealed class ExpressionHoldTests
{
    private static readonly ActorId Actor =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);

    /// <summary>The facial timeline the actor arrives with — the one thing
    /// restoration must put back.</summary>
    private const ushort Incoming = 777;

    private const ushort Smile = 9001;
    private const ushort Frown = 9002;

    [Fact]
    public void Reapplying_expression_holds_zero_speed_and_keeps_first_restore_point()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.LiveFacialTimeline = Smile;
        Assert.True(session.HoldExpression(Actor, Frown).Success);

        Assert.Equal(Frown, session.HeldExpressionFor(Actor));
        Assert.Equal(Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);
        Assert.Equal(0f,
            session.OverridesFor(Actor).SlotSpeeds[AnimationSlot.Facial]);
        Assert.Equal(1f,
            session.OverridesFor(Actor).SlotSpeedCaptures[AnimationSlot.Facial]);
        Assert.Equal(2, port.Calls.Count(
            call => call == "SetSlotSpeed:Facial:0"));
    }

    [Fact]
    public void Expression_release_failure_preserves_retryable_ownership()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.BlendFailure = "facial restore unavailable";
        Assert.False(session.ReleaseExpression(Actor).Success);
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Equal(Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);

        port.BlendFailure = null;
        Assert.True(session.RestoreFacialLayer(Actor).Success);
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.DoesNotContain(
            AnimationSlot.Facial,
            session.OverridesFor(Actor).SlotCaptures.Keys);
        Assert.Contains(port.Calls,
            call => call.StartsWith("ClearSlotSpeed:Facial:"));
    }

    [Fact]
    public void Stale_expression_apply_keeps_selection_without_replacement_writes()
    {
        using var app = new CoordinatorHarness();
        Assert.True(app.Hold.Begin(Actor, Smile).Success);
        app.ReplaceSkeleton();
        int beforeCallback = app.Port.Calls.Count;

        app.Framework.FireUpdate();

        Assert.False(app.Hold.IsPendingFor(Actor));
        Assert.Equal(Smile,
            app.Animation.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.Null(app.Animation.HeldExpressionFor(Actor));
        Assert.Equal(beforeCallback, app.Port.Calls.Count);
    }

    [Fact]
    public void Hold_failure_restores_facial_selection_before_refusing()
    {
        var port = FakePort.Create();
        port.SpeedFailure = "facial speed unavailable";
        var session = new AnimationSession(port.Port);

        var result = session.HoldExpression(Actor, Smile);

        Assert.False(result.Success);
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.Null(session.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.Equal(Incoming, port.LiveFacialTimeline);
        Assert.Equal($"Blend:{Incoming}",
            port.Calls.Last(call => call.StartsWith("Blend:")));
    }

    [Fact]
    public void Paused_expression_preview_and_reset_restore_exact_facial_state()
    {
        using var app = new CoordinatorHarness();
        Assert.True(app.Animation.Pause(Actor).Success);
        app.Port.Calls.Clear();

        Assert.True(app.Hold.Begin(Actor, Smile).Success);
        app.Framework.FireUpdate();
        Assert.Null(app.Animation.HeldExpressionFor(Actor));
        app.SetFace(1);
        app.Framework.FireUpdate();
        app.Framework.FireUpdate();
        app.Framework.FireUpdate();
        Assert.Equal(Smile, app.Animation.HeldExpressionFor(Actor));
        Assert.True(app.Hold.Release(Actor).Success);

        Assert.Equal(Incoming, app.Port.LiveFacialTimeline);
        Assert.True(app.Animation.OverridesFor(Actor).IsPaused);
        Assert.Contains("SetSlotSpeed:Facial:1", app.Port.Calls);
        Assert.Contains("Blend:604", app.Port.Calls);
        Assert.Equal(
            ["ClearSlotSpeed:Facial:1", "Blend:777"],
            app.Port.Calls.TakeLast(2));
        Assert.Null(app.Animation.HeldExpressionFor(Actor));
        Assert.Null(app.Animation.SelectedFor(Actor, AnimationSlot.Facial));

        Assert.True(app.Hold.Begin(Actor, Smile).Success);
        Assert.True(app.Hold.IsPendingFor(Actor));
        Assert.True(app.Hold.Release(Actor).Success);
        Assert.False(app.Hold.IsPendingFor(Actor));
        Assert.Equal(Incoming, app.Port.LiveFacialTimeline);
        Assert.True(app.Animation.OverridesFor(Actor).IsPaused);
    }

    [Fact]
    public void Paused_expression_reset_failure_keeps_ownership_for_retry()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.Pause(Actor).Success);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.BlendFailure = "facial restore unavailable";

        Assert.False(session.ReleaseExpression(Actor).Success);

        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Equal(Smile, session.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.Equal(Incoming,
            session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);
        Assert.True(session.OverridesFor(Actor).IsPaused);

        port.BlendFailure = null;
        Assert.True(session.ReleaseExpression(Actor).Success);
        Assert.Null(session.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.True(session.OverridesFor(Actor).IsPaused);
    }

    private sealed class CoordinatorHarness : IDisposable
    {
        private readonly BoneProxy _bone;
        private ulong _revision = 1;

        public CoordinatorHarness()
        {
            var skeleton = new SkeletonId(Actor, PoseSlot.Character, 4);
            Bone = new BoneId(skeleton, 0, 7, "j_kao");
            Scene = new SceneSession(new SelectionSession());
            Scene.Refresh(Snapshot(Describe(skeleton, Bone), _revision));
            Bindings = (StableBindingRegistry)RuntimeHelpers
                .GetUninitializedObject(typeof(StableBindingRegistry));
            SetField(Bindings, "_actorBindings", new Dictionary<ActorId, IActor>
            {
                [Actor] = DispatchProxy.Create<IActor, DefaultProxy>(),
            });
            var bone = DispatchProxy.Create<IBone, BoneProxy>();
            _bone = (BoneProxy)(object)bone;
            _bone.Raw = Raw(0);
            SetField(Bindings, "_boneBindings", new Dictionary<BoneId, IBone>
            {
                [Bone] = bone,
            });
            Framework = FrameworkProxy.Create();
            Port = FakePort.Create();
            Animation = new AnimationSession(Port.Port);
            Session = new MutableSessionSource
            {
                Active = SessionGeneration.Create(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            };
            Hold = new ExpressionHoldCoordinator(
                Framework.Framework,
                Bindings,
                Scene,
                Animation,
                DispatchProxy.Create<IBonePosingService, DefaultProxy>(),
                Session,
                DispatchProxy.Create<IPluginLog, DefaultProxy>());
        }

        public SceneSession Scene { get; }
        public BoneId Bone { get; }
        public StableBindingRegistry Bindings { get; }
        public FrameworkProxy Framework { get; }
        public FakePort Port { get; }
        public AnimationSession Animation { get; }
        public MutableSessionSource Session { get; }
        public ExpressionHoldCoordinator Hold { get; }

        public void ReplaceSkeleton()
        {
            var replacement = new SkeletonId(Actor, PoseSlot.Character, 5);
            var replacementBone = new BoneId(replacement, 0, 7, "j_kao");
            Scene.Refresh(Snapshot(
                Describe(replacement, replacementBone), ++_revision));
        }

        public void SetFace(float x) => _bone.Raw = Raw(x);

        public void Dispose() => Hold.Dispose();

        private static ActorDescriptor Describe(
            SkeletonId skeleton, BoneId bone) =>
            new(Actor, "Actor",
                [new SkeletonDescriptor(skeleton,
                    [new BoneDescriptor(bone, bone.CanonicalName, null)])]);

        private static SceneSnapshot Snapshot(
            ActorDescriptor actor, ulong revision) =>
            new(revision, [actor], [], [], []);

        private static Poser.Transform Raw(float x) =>
            new(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);
    }

    private class BoneProxy : DispatchProxy
    {
        public Poser.Transform Raw { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "get_LastRawTransform"
                ? Raw
                : Default(method?.ReturnType);
    }

    private sealed class MutableSessionSource : ISessionGenerationSource
    {
        public SessionGeneration? Active { get; set; }
        public SessionGeneration? ActiveSessionGeneration => Active;
    }

    private class FrameworkProxy : DispatchProxy
    {
        private Delegate? _update;
        public IFramework Framework { get; private set; } = null!;

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

    private class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Default(method?.ReturnType);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static object? Default(Type? type)
    {
        if (type == null || type == typeof(void))
            return null;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();

        /// <summary>What the facial slot is showing right now.</summary>
        public ushort LiveFacialTimeline { get; set; } = Incoming;

        public string? BlendFailure { get; set; }
        public string? SpeedFailure { get; set; }
        public static FakePort Create()
        {
            var port = DispatchProxy.Create<IAnimationRuntimePort, FakePort>();
            var proxy = (FakePort)(object)port;
            proxy.Port = port;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "IsSupported":
                    return true;
                case "Read":
                    return new ActorAnimationReading(
                        0, 1f, 0, false, AnimationStance.Idle, 0,
                        new[]
                        {
                            new AnimationSlotReading(
                                AnimationSlot.Facial, LiveFacialTimeline, 1f),
                        },
                        Array.Empty<ScrubControlReading>(),
                        1UL);
                case "TimelineSlot":
                {
                    // The sheet's routing, reduced to this flow: idle is a
                    // base timeline, everything else here is facial.
                    ushort timeline = (ushort)args![0]!;
                    return (AnimationSlot?)(timeline == AnimationTimelines.Idle
                        ? AnimationSlot.Base
                        : AnimationSlot.Facial);
                }
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    LiveFacialTimeline = (ushort)args[1]!;
                    args[3] = null;
                    return BlendFailure is { } refusal
                        ? AnimationPortResult.Fail(refusal)
                        : AnimationPortResult.Ok();
                case "SetSlotSpeed":
                    Calls.Add(
                        $"SetSlotSpeed:{(AnimationSlot)args![1]!}:{(float)args[2]!}");
                    return SpeedFailure is { } speedRefusal
                        ? AnimationPortResult.Fail(speedRefusal)
                        : AnimationPortResult.Ok();
                case "ClearSlotSpeed":
                    Calls.Add(
                        $"ClearSlotSpeed:{(AnimationSlot)args![1]!}:" +
                        $"{(float)args[2]!}");
                    return AnimationPortResult.Ok();
                default:
                    if (method?.ReturnType == typeof(AnimationPortResult))
                    {
                        Calls.Add(method.Name);
                        return AnimationPortResult.Ok();
                    }
                    if (method?.ReturnType is { IsValueType: true } type &&
                        type != typeof(void))
                        return Activator.CreateInstance(type);
                    return null;
            }
        }
    }
}
