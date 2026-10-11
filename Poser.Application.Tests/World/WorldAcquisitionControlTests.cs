using System.Reflection;
using Poser.Application.Lifecycle;
using Poser.Application.Selection;
using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.World;

public sealed class WorldAcquisitionControlTests
{
    [Fact]
    public void Completion_never_enters_a_replacement_session()
    {
        var service = DispatchProxy.Create<IWorldService, World>();
        var world = (World)(object)service;
        var sessions = new Sessions();
        var selection = new SelectionSession();
        var failures = new Fixtures.NoticeLog();
        var control = new WorldAcquisitionControl(service, sessions, selection, failures);
        var target = SelectionId.ForActor(ActorId.New());
        control.Acquire(new(Guid.NewGuid()));
        sessions.ActiveSessionGeneration = SessionGeneration.New();
        world.Complete.SetResult(new(WorldCommandStatus.Applied, new(Guid.NewGuid()), target));
        control.Tick();
        Assert.Null(selection.Primary);
        Assert.Empty(failures);
    }

    private sealed class Sessions : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; set; } = SessionGeneration.New();
    }
    public class World : DispatchProxy
    {
        public TaskCompletionSource<WorldAcquisition> Complete = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IWorldService.Acquire) ? Complete.Task : throw new NotSupportedException();
    }
}
