using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Game.World;

namespace Poser.Game.Tests.World;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_or_session_cancellation_rolls_back_without_history(bool cancel)
    {
        var f = new Fixture();
        if (!cancel)
            for (int tick = 0; tick < 119; tick++) Assert.False(f.Pending.Tick(true));
        Assert.True(f.Pending.Tick(!cancel));
        Assert.Equal(new[] { "rollback" }, f.Events);
        Assert.False((await f.Pending.Completion).Success);
        Assert.Null((await f.Pending.Completion).Claim);
        f.Bound = true;
        Assert.True(f.Pending.Tick(true));
        Assert.Equal(new[] { "rollback" }, f.Events);
    }

    [Fact]
    public async Task Resolution_failure_cleans_up_instead_of_leaving_a_faulted_wait()
    {
        var f = new Fixture { ThrowResolve = true };
        Assert.True(f.Pending.Tick(true));
        Assert.Contains("binding failed", (await f.Pending.Completion).Detail);
        Assert.Equal(new[] { "rollback" }, f.Events);
    }

    [Fact]
    public async Task Refused_cleanup_is_retained_and_cannot_later_commit()
    {
        var f = new Fixture { AllowRollback = false };
        Assert.False(f.Pending.Tick(false));
        Assert.Contains("Cleanup is still pending", (await f.Pending.Completion).Detail);
        f.Bound = true;
        Assert.False(f.Pending.Tick(true));
        Assert.Single(f.Warnings);
        f.AllowRollback = true;
        Assert.True(f.Pending.Tick(true));
        Assert.Equal(new[] { "rollback", "rollback", "rollback" }, f.Events);
    }

    [Fact]
    public async Task Late_binding_from_replacement_session_cannot_admit_a_claim()
    {
        var f = new Fixture { Bound = true };
        Assert.True(f.Pending.Tick(false));
        Assert.False((await f.Pending.Completion).Success);
        Assert.Equal(new[] { "rollback" }, f.Events);
    }

    private sealed class Fixture
    {
        public bool Bound, ThrowResolve;
        public bool AllowRollback = true;
        public readonly List<string> Events = [];
        public readonly List<string> Warnings = [];
        public readonly PendingWorldAcquisition Pending;

        public Fixture() => Pending = new(new(
            () => ThrowResolve ? throw new InvalidOperationException("binding failed")
                : Bound ? SelectionId.ForEnvironment() : null,
            () => Events.Add("commit"),
            () => { Events.Add("rollback"); return AllowRollback; }),
            entity => { Events.Add("claim"); return new(WorldCommandStatus.Applied, new(Guid.NewGuid()), entity); },
            Warnings.Add);
    }
}
