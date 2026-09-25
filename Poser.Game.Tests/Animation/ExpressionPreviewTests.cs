using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Animation;
using Poser.Services;

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

    [Theory]
    [InlineData("binding")]
    [InlineData("session")]
    [InlineData("selection")]
    [InlineData("reset")]
    [InlineData("dispose")]
    public void Retry_cannot_outlive_its_target_or_command(string change)
    {
        using var f = new Fixture();
        f.Preview();
        switch (change)
        {
            case "binding": f.Body = Stub<IActor>((_, _) => null); break;
            case "session": f.Session = SessionGeneration.New(); break;
            // Even an alias of the same timeline cancels the previous request.
            case "selection": Assert.True(f.Control.Choose(f.Actor, f.Entry with { Name = "Alias" }).Success); break;
            case "reset": Assert.True(f.Control.Reset(f.Actor).Success); break;
            case "dispose": f.Control.Dispose(); break;
        }
        var before = f.Plays;
        f.Tick(1000);
        Assert.Equal(before, f.Plays);
        Assert.False(f.Control.IsPending(f.Actor));
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
        f.OnThread = false;
        var writes = f.Plays;
        Assert.False(f.Control.Bake(f.Actor, 45).Success);
        Assert.False(f.Control.Choose(f.Actor, f.Entry).Success);
        Assert.Equal(writes, f.Plays);
        Assert.Null(f.BakedActor);
        f.OnThread = true;
        Assert.False(f.Control.Bake(f.Actor.NextGeneration(), 45).Success);
        Assert.Equal(writes, f.Plays);
    }

    [Fact]
    public void Failed_retry_reports_once_and_does_not_loop()
    {
        using var f = new Fixture();
        f.Preview();
        f.FailBlend = true;
        var failures = new List<string>();
        f.Control.Failed += failures.Add;
        f.Tick(500);
        f.Tick(500);
        Assert.Single(failures);
        Assert.Contains("Expression retry", failures[0]);
        Assert.False(f.Control.IsPending(f.Actor));
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly TimelineEntry Entry = new(45, "Expression", AnimationKind.Expression, AnimationSlot.Facial);
        public SessionGeneration? Session = SessionGeneration.New();
        public IActor Body = Stub<IActor>((_, _) => null);
        public bool OnThread = true;
        public bool FailBlend;
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
                    case "get_IsInFrameworkUpdateThread": return OnThread;
                    case "add_Update": _update = Delegate.Combine(_update, (Delegate)a![0]!); break;
                    case "remove_Update": _update = Delegate.Remove(_update, (Delegate)a![0]!); break;
                }
                return null;
            });
            var port = Stub<IAnimationRuntimePort>((m, a) =>
            {
                if (m.Name == "Read") return ActorAnimationReading.Empty;
                if (m.Name == "TimelineSlot") return AnimationSlot.Facial;
                if (m.Name == "Blend")
                {
                    Plays++;
                    a![3] = null;
                    return FailBlend ? AnimationPortResult.Fail("test failure") : AnimationPortResult.Ok();
                }
                if (m.ReturnType == typeof(AnimationPortResult)) return AnimationPortResult.Ok();
                if (m.ReturnType == typeof(bool)) return true;
                return null;
            });
            Control = new ExpressionPreview(
                _framework,
                Stub<IEntityBindings>((m, a) => new BindingResult<IActor>(
                    BindingStatus.Success, (ActorId)a![0]! == Actor ? Body : null)),
                Stub<ISessionGenerationSource>((_, _) => Session),
                scene, new AnimationSession(port),
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
