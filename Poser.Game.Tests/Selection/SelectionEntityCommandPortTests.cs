using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Selection;
using Poser.Game.World;
using Poser.Services;

namespace Poser.Game.Tests.Selection;

public sealed class SelectionEntityCommandPortTests
{
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
        Assert.Equal(1, fixture.Framework.Dispatches);
    }

    [Theory]
    [InlineData(WorldCommandStatus.Applied, SelectionRemovalStatus.Removed, 1, false)]
    [InlineData(WorldCommandStatus.AlreadyReleased, SelectionRemovalStatus.AlreadyAbsent, 0, true)]
    [InlineData(WorldCommandStatus.Refused, SelectionRemovalStatus.Refused, 0, true)]
    public async Task Borrowed_light_uses_exact_release_port_and_reports_its_outcome(
        WorldCommandStatus releaseStatus, SelectionRemovalStatus expected, int applied, bool selected)
    {
        var fixture = new Fixture();
        fixture.Release.Result = new(releaseStatus, "Release detail");
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
        if (expected == SelectionRemovalStatus.Refused)
            Assert.Equal("Release detail", item.Detail);
    }

    [Fact]
    public async Task Release_exception_is_a_failed_item_and_keeps_selection()
    {
        var fixture = new Fixture();
        fixture.Release.Throw = true;
        var pending = fixture.Remove();
        fixture.Framework.Run();
        var result = await pending;
        var item = Assert.Single(result.Items);
        Assert.Equal(SelectionRemovalStatus.Failed, item.Status);
        Assert.Equal("Release failed", item.Detail);
        Assert.Equal(0, result.AppliedCount);
        Assert.True(fixture.Scene.Selection.IsSelected(fixture.Id));
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
        private readonly SelectionEntityCommandPort _port;

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
                _ => throw new InvalidOperationException(method.Name),
            });
            var bindings = Proxy<IEntityBindings>((method, args) => method.Name switch
            {
                "GetLightId" when ReferenceEquals(args![0], light) => (LightId?)Light,
                "Resolve" when args![0] is LightId id && id == Light =>
                    new BindingResult<ILight>(BindingStatus.Success, light),
                _ => throw new InvalidOperationException(method.Name),
            });
            var lighting = Proxy<ILightingService>((method, _) => method.Name == "get_Lights"
                ? new ILight[] { light } : throw new InvalidOperationException(method.Name));
            var framework = DispatchProxy.Create<IFramework, FrameworkProxy>();
            Framework = (FrameworkProxy)(object)framework;
            _port = new SelectionEntityCommandPort(Scene, bindings, null!, null!, null!,
                null!, lighting, null!, null!, null!, null!, Release, Groups, framework, History);
        }

        public Task<SelectionRemovalResult> Remove() =>
            _port.Remove([new(Id, SelectionRemoval.Release)]);
    }

    private sealed class ReleasePort : IWorldReleasePort
    {
        public int Calls;
        public SelectionId? Requested;
        public bool Throw;
        public WorldRelease Result = new(WorldCommandStatus.Applied);

        public WorldRelease ReleaseCurrent(SelectionId entity)
        {
            Calls++;
            Requested = entity;
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
        public int Dispatches;
        public void Run() => _run!();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("RunOnFrameworkThread", method!.Name);
            Dispatches++;
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
