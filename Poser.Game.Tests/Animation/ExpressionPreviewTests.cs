using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Animation;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Animation;

public sealed class ExpressionPreviewTests
{
    [Fact]
    public void Preview_retries_once_without_any_panel_being_drawn()
    {
        using var f = new Fixture();
        f.Preview();
        Assert.Equal(1, f.Plays);
        f.Tick(499);
        Assert.Equal(1, f.Plays);
        f.Tick(1);
        Assert.Equal(2, f.Plays);
        Assert.False(f.Control.IsPending(f.Actor));
        f.Tick(1000);
        Assert.Equal(2, f.Plays);
    }

    [Fact]
    public void Bake_uses_exact_actor_and_refuses_pending_or_unavailable_targets()
    {
        using var f = new Fixture();
        f.Preview();
        Assert.False(f.Control.Bake(f.Actor, 45).Success);
        Assert.Null(f.BakedActor);
        f.Tick(500);
        Assert.True(f.Control.Bake(f.Actor, 45).Success);
        Assert.Equal(f.Actor, f.BakedActor);
        Assert.Equal(f.Actor, f.BakedDescriptor!.Id);
        f.BakedActor = null;
        var writes = f.Plays;
        Assert.False(f.Control.Bake(f.Actor.NextGeneration(), 45).Success);
        Assert.Equal(writes, f.Plays);
        Assert.Null(f.BakedActor);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly TimelineEntry Entry = new(45, "Expression", AnimationKind.Expression, AnimationSlot.Facial);
        public readonly SessionGeneration? Session = SessionGeneration.New();
        public readonly IActor Body = Stub<IActor>((_, _) => null);
        public int Plays;
        public ActorId? BakedActor;
        public ActorDescriptor? BakedDescriptor;
        public readonly ExpressionPreview Control;
        private readonly Clock _clock = new();
        private readonly IFramework _framework;
        private Delegate? _update;

        public Fixture()
        {
            var scene = new SceneSession(new SelectionSession());
            scene.Refresh(new SceneSnapshot(1, [new ActorDescriptor(Actor, "Actor", [])], [], [], []));
            _framework = Stub<IFramework>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_IsInFrameworkUpdateThread": return true;
                    case "add_Update": _update = Delegate.Combine(_update, (Delegate)a![0]!); break;
                    case "remove_Update": _update = Delegate.Remove(_update, (Delegate)a![0]!); break;
                }
                return null;
            });
            Func<MethodInfo, object?[]?, object?> port = (m, a) =>
            {
                if (m.Name == "Read") return ActorAnimationReading.Empty;
                if (m.Name == "TimelineSlot") return AnimationSlot.Facial;
                if (m.Name == "Blend")
                {
                    Plays++;
                    a![3] = null;
                    return Outcome.Ok();
                }
                if (m.ReturnType == typeof(Outcome)) return Outcome.Ok();
                if (m.ReturnType == typeof(bool)) return true;
                return null;
            };
            Control = new ExpressionPreview(
                _framework,
                Stub<IEntityBindings>((m, a) => new BindingResult<IActor>(
                    BindingStatus.Success, (ActorId)a![0]! == Actor ? Body : null)),
                Stub<ISessionGenerationSource>((_, _) => Session),
                scene, new AnimationSession(Stub<IAnimationTimelinePort>(port),
                    Stub<IAnimationSpeedPort>(port), Stub<IAnimationStancePort>(port),
                    Stub<IAnimationScrubPort>(port), Stub<IWorldRenderingRuntimePort>(port)),
                Stub<IFacialPoseCapture>((m, a) =>
                {
                    if (m.Name == "get_IsPending") return false;
                    BakedActor = (ActorId)a![0]!;
                    BakedDescriptor = (ActorDescriptor)a[1]!;
                    return GestureResult.Ok();
                }), _clock);
        }

        public void Preview()
        {
            Assert.True(Control.Choose(Actor, Entry).Success);
            Assert.True(Control.Preview(Actor, 45).Success);
            Assert.True(Control.IsPending(Actor));
        }
        public void Tick(long milliseconds)
        {
            _clock.Now += milliseconds;
            _update?.DynamicInvoke(_framework);
        }
        public void Dispose() => Control.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
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
