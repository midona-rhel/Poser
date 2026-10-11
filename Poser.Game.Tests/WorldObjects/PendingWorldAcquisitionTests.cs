using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Game.WorldObjects;

namespace Poser.Game.Tests.WorldObjects;

public sealed class PendingWorldAcquisitionTests
{
    [Fact]
    public async Task Binding_publication_commits_once_without_rollback()
    {
        var f = new Fixture();
        Assert.False(f.Pending.Tick(true));
        Assert.Empty(f.Events);
        Assert.False(f.Pending.Completion.IsCompleted);
        f.Bound = true;
        Assert.True(f.Pending.Tick(true));
        Assert.Equal(new[] { "commit", "claim" }, f.Events);
        Assert.True((await f.Pending.Completion).Success);
        Assert.True(f.Pending.Tick(false));
        Assert.Equal(2, f.Events.Count);
    }

    [Fact]
    public async Task Session_cancellation_rolls_back_without_history_even_if_binding_arrives_late()
    {
        var f = new Fixture { Bound = true };
        Assert.True(f.Pending.Tick(false));
        Assert.Equal(new[] { "rollback" }, f.Events);
        Assert.False((await f.Pending.Completion).Success);
        Assert.Null((await f.Pending.Completion).Claim);
        Assert.True(f.Pending.Tick(true));
        Assert.Equal(new[] { "rollback" }, f.Events);
    }

    private sealed class Fixture
    {
        public bool Bound;
        public readonly List<string> Events = [];
        public readonly PendingWorldAcquisition Pending;

        public Fixture() => Pending = new(new(
            () => Bound ? SelectionId.ForEnvironment() : null,
            () => Events.Add("commit"),
            () => { Events.Add("rollback"); return true; }),
            entity => { Events.Add("claim"); return new(WorldCommandStatus.Applied, new(Guid.NewGuid()), entity); },
            _ => { });
    }
}
