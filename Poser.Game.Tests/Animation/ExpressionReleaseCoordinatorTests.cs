using System.Reflection;
using System.Runtime.CompilerServices;
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

namespace Poser.Game.Tests.Animation;

/// <summary>Generation-safe ordering for the delayed Straight Face bridge.</summary>
public sealed class ExpressionReleaseCoordinatorTests
{
    [Fact]
    public void Completion_waits_two_ticks_then_clears_ownership()
    {
        using var app = new Harness();
        Assert.True(app.Animation.HoldExpression(app.Actor, 9001).Success);
        Assert.True(app.Release.Begin(app.Actor).Success);

        app.Framework.FireUpdate();
        Assert.NotNull(app.Animation.HeldExpressionFor(app.Actor));

        app.Framework.FireUpdate();
        Assert.Null(app.Animation.HeldExpressionFor(app.Actor));
        Assert.Null(app.Animation.SelectedFor(app.Actor, AnimationSlot.Facial));
        Assert.Equal(
            ["Blend:604", "Blend:777"],
            app.Port.Calls.Where(call => call.StartsWith("Blend:"))
                .TakeLast(2));
    }

    [Fact]
    public void Replaced_skeleton_cancels_callback_without_clearing_ownership()
    {
        using var app = new Harness();
        Assert.True(app.Animation.HoldExpression(app.Actor, 9001).Success);
        Assert.True(app.Release.Begin(app.Actor).Success);
        app.ReplaceSkeleton();

        app.Framework.FireUpdate();

        Assert.False(app.Release.IsPendingFor(app.Actor));
        Assert.Equal((ushort)9001,
            app.Animation.HeldExpressionFor(app.Actor));
        Assert.Equal((ushort)9001,
            app.Animation.SelectedFor(app.Actor, AnimationSlot.Facial));
        Assert.DoesNotContain("Blend:777", app.Port.Calls);
    }

    private sealed class Harness : IDisposable
    {
        private ulong _revision = 1;

        public Harness()
        {
            Actor = new ActorId(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
            Skeleton = new SkeletonId(Actor, PoseSlot.Character, 4);
            Scene = new SceneSession(new SelectionSession());
            Scene.Refresh(Snapshot(Describe(Skeleton), _revision));

            Bindings = (StableBindingRegistry)RuntimeHelpers
                .GetUninitializedObject(typeof(StableBindingRegistry));
            var actorProxy = DispatchProxy.Create<IActor, DefaultProxy>();
            SetField(Bindings, "_actorBindings", new Dictionary<ActorId, IActor>
            {
                [Actor] = actorProxy,
            });

            Framework = FrameworkProxy.Create();
            Port = AnimationPortProxy.Create();
            Animation = new AnimationSession(Port.Port);
            Session = new MutableSessionSource
            {
                Active = SessionGeneration.Create(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            };
            Release = new ExpressionReleaseCoordinator(
                Framework.Framework,
                Bindings,
                Scene,
                Animation,
                Session,
                DispatchProxy.Create<IPluginLog, DefaultProxy>());
        }

        public ActorId Actor { get; }
        public SkeletonId Skeleton { get; }
        public SceneSession Scene { get; }
        public StableBindingRegistry Bindings { get; }
        public FrameworkProxy Framework { get; }
        public AnimationPortProxy Port { get; }
        public AnimationSession Animation { get; }
        public MutableSessionSource Session { get; }
        public ExpressionReleaseCoordinator Release { get; }

        public void ReplaceSkeleton()
        {
            var replacement = new SkeletonId(Actor, PoseSlot.Character, 5);
            Scene.Refresh(Snapshot(Describe(replacement), ++_revision));
        }

        public void Dispose() => Release.Dispose();

        private ActorDescriptor Describe(SkeletonId skeleton) =>
            new(Actor, "Actor",
                [new SkeletonDescriptor(skeleton, Array.Empty<BoneDescriptor>())]);

        private static SceneSnapshot Snapshot(
            ActorDescriptor actor, ulong revision) =>
            new(revision, [actor], [], [], []);
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

    private class AnimationPortProxy : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();
        private ushort _facial = 777;

        public static AnimationPortProxy Create()
        {
            var port = DispatchProxy
                .Create<IAnimationRuntimePort, AnimationPortProxy>();
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
                case "Read":
                    return ActorAnimationReading.Empty with
                    {
                        OverallSpeed = 1f,
                        Slots = [new AnimationSlotReading(
                            AnimationSlot.Facial, _facial, 1f)],
                    };
                case "TimelineSlot":
                    return (AnimationSlot?)AnimationSlot.Facial;
                case "Blend":
                    _facial = (ushort)args![1]!;
                    Calls.Add($"Blend:{_facial}");
                    args[3] = null;
                    return AnimationPortResult.Ok();
                case "SetSlotSpeed":
                    Calls.Add($"Speed:{args![2]}");
                    return AnimationPortResult.Ok();
                case "ClearSlotSpeed":
                    Calls.Add($"ClearSpeed:{args![2]}");
                    return AnimationPortResult.Ok();
                default:
                    if (method?.ReturnType == typeof(AnimationPortResult))
                        return AnimationPortResult.Ok();
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
}
