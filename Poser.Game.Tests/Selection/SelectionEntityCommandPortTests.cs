using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Journal;
using Poser.Game.Lighting;
using Poser.Game.Selection;
using Poser.Game.WorldObjects;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Selection;

public sealed class SelectionEntityCommandPortTests
{
    [Fact]
    public async Task Normal_light_removal_keeps_unrelated_actor_history_reachable()
    {
        var fixture = new Fixture();
        var actor = SelectionId.ForActor(ActorId.New());
        var edit = new JournalStep("Actor edit", () => true, () => true)
            { AffectedEntities = new[] { actor } };
        fixture.History.Append(edit);
        fixture.Release.OnRelease = () => fixture.History.Append(
            new SceneLifecyclePatch("Release light", () => true, () => true)
                { AffectedEntities = new[] { fixture.Id } });
        var pending = fixture.Remove();
        fixture.Framework.Run();
        Assert.Equal(1, (await pending).AppliedCount);
        Assert.False(fixture.Scene.Selection.IsSelected(fixture.Id));
        var removal = Assert.IsType<SceneLifecyclePatch>(fixture.History.PeekUndo());
        Assert.Equal(fixture.Id, Assert.Single(removal.ResolveAffectedEntities!()!));
        Assert.Same(edit, fixture.History.PeekUndo(actor));
        Assert.Null(fixture.History.PeekUndo(fixture.Id));
        fixture.History.CommitUndo(edit, actor);
        Assert.True(removal.Undo());
        fixture.History.CommitUndo(removal);
        Assert.Same(edit, fixture.History.PeekRedo(actor));
        Assert.Null(fixture.History.PeekRedo(fixture.Id));
        Assert.True(removal.Redo());
        fixture.History.CommitRedo(removal);
    }

    [Fact]
    public async Task Group_locked_after_dispatch_is_rechecked_before_release()
    {
        var fixture = new Fixture();
        var group = fixture.Groups.Create("Protected", [fixture.Id], allowThin: true)!;
        var pending = fixture.Remove();
        Assert.False(pending.IsCompleted);
        fixture.Groups.SetLocked(group.Id, true);
        fixture.Framework.Run();
        var result = await pending;
        Assert.Equal(SelectionRemovalStatus.Refused, Assert.Single(result.Items).Status);
        Assert.Equal(0, fixture.Release.Calls);
        Assert.True(fixture.Scene.Selection.IsSelected(fixture.Id));
        Assert.False(fixture.History.CanUndo);
    }

    [Theory]
    [InlineData(WorldCommandStatus.Applied, false, SelectionRemovalStatus.Removed, 1, false)]
    [InlineData(WorldCommandStatus.Refused, false, SelectionRemovalStatus.Refused, 0, true)]
    [InlineData(WorldCommandStatus.Applied, true, SelectionRemovalStatus.Failed, 0, true)]
    public async Task Borrowed_light_uses_exact_release_port_and_reports_its_outcome(
        WorldCommandStatus releaseStatus, bool throws, SelectionRemovalStatus expected, int applied, bool selected)
    {
        var fixture = new Fixture();
        fixture.Release.Result = new(releaseStatus);
        fixture.Release.Throw = throws;
        var pending = fixture.Remove();
        fixture.Framework.Run();
        var result = await pending;
        var item = Assert.Single(result.Items);
        Assert.Equal(fixture.Id, item.Id);
        Assert.Equal(expected, item.Status);
        Assert.Equal(applied, result.AppliedCount);
        Assert.Equal(1, fixture.Release.Calls);
        Assert.Equal(fixture.Id, fixture.Release.Requested);
        Assert.Equal(selected, fixture.Scene.Selection.IsSelected(fixture.Id));
    }

    [Fact]
    public void Refused_light_visibility_reports_detail_and_records_nothing()
    {
        var fixture = new Fixture();
        var earlier = new JournalStep("Earlier edit", () => true, () => true);
        fixture.History.Append(earlier);
        fixture.History.CommitUndo(earlier);

        var result = fixture.Port.SetVisibility(fixture.Id, visible: false);

        Assert.False(result.Success);
        Assert.Equal("The light refused.", result.Detail);
        Assert.False(fixture.History.CanUndo);
        Assert.Same(earlier, fixture.History.PeekRedo());
    }

    private sealed class Fixture
    {
        public readonly LightId Light = LightId.New();
        public SelectionId Id => SelectionId.ForLight(Light);
        public readonly SceneSession Scene = new(new SelectionSession());
        public readonly SceneGroups Groups = new();
        public readonly TransformHistory History = new();
        public readonly ReleasePort Release = new();
        public readonly FrameworkProxy Framework;
        public readonly SelectionEntityCommandPort Port;

        public Fixture()
        {
            Assert.True(Scene.TryRefresh(new SceneSnapshot(1, [],
                [new LightDescriptor(Light, "Borrowed", LightKind.Point, Ownership: LightOwnership.World)],
                [], [])).Accepted);
            Scene.Selection.Select(Id);
            var light = Proxy<ILight>((method, _) => method.Name switch
            {
                "get_IsValid" => true,
                "get_Ownership" => LightOwnership.World,
                "get_IsOn" => true,
                "set_IsOn" => throw new InvalidOperationException("The light refused."),
                _ => throw new InvalidOperationException(method.Name),
            });
            var bindings = Proxy<IEntityBindings>((method, args) => method.Name switch
            {
                "GetLightId" when ReferenceEquals(args![0], light) => (LightId?)Light,
                "Resolve" when args![0] is LightId id && id == Light =>
                    new BindingResult<ILight>(BindingStatus.Success, light),
                _ => throw new InvalidOperationException(method.Name),
            });
            var lighting = Proxy<ILightingService>((method, _) => method.Name switch
            {
                "get_Lights" => new ILight[] { light },
                "get_Gobos" => Array.Empty<GoboEntry>(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var framework = DispatchProxy.Create<IFramework, FrameworkProxy>();
            Framework = (FrameworkProxy)(object)framework;
            var lights = new LightControl(bindings, lighting, new ValueJournal(History), null!);
            Port = new SelectionEntityCommandPort(Scene, bindings, null!, null!, lights, null!, null!, null!,
                null!, lighting, null!, null!, null!, null!, Release, Groups, framework, History);
        }

        public Task<SelectionRemovalResult> Remove() =>
            Port.Remove([new(Id, SelectionRemoval.Release)]);
    }

    private sealed class ReleasePort : IWorldReleasePort
    {
        public int Calls;
        public SelectionId? Requested;
        public bool Throw;
        public Action? OnRelease;
        public WorldRelease Result = new(WorldCommandStatus.Applied);

        public WorldRelease ReleaseCurrent(SelectionId entity)
        {
            Calls++;
            Requested = entity;
            if (!Throw && Result.Status == WorldCommandStatus.Applied) OnRelease?.Invoke();
            return Throw ? throw new InvalidOperationException("Release failed") : Result;
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, CallbackProxy>();
        ((CallbackProxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class CallbackProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    public class FrameworkProxy : DispatchProxy
    {
        private Action? _run;
        public void Run() => _run!();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("RunOnFrameworkThread", method!.Name);
            var completion = new TaskCompletionSource<SelectionRemovalResult>();
            var callback = (Func<SelectionRemovalResult>)args![0]!;
            _run = () =>
            {
                try { completion.SetResult(callback()); }
                catch (Exception ex) { completion.SetException(ex); }
            };
            return completion.Task;
        }
    }
}
