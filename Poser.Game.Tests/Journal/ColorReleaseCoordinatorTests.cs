using System.Numerics;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Presentation;

namespace Poser.Game.Tests.Journal;

public sealed class ColorReleaseCoordinatorTests
{
    [Fact]
    public void Unrelated_event_is_ignored_and_only_target_is_suspended_until_later_readiness()
    {
        var f = new Fixture(); f.Begin();
        f.Coordinator.Redrawn(999, f.Target.Index);
        f.Coordinator.Redrawn(f.Target.Address, 999);
        f.Tick(); f.Tick();
        Assert.True(f.Coordinator.IsPending(f.Actor));
        Assert.All(f.Writes, channel => Assert.Equal(AppearanceColorChannel.Hair, channel));
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index);
        f.Readable = false; f.Tick(); f.Tick();
        Assert.True(f.Coordinator.IsPending(f.Actor));
        Assert.Equal(0, f.Releases);
        f.Readable = true; f.Tick();
        Assert.False(f.Coordinator.IsPending(f.Actor));
        Assert.True(Assert.Single(f.Results).Success);
        Assert.Equal(1, f.Releases);
        Assert.False(f.Values.ContainsKey(AppearanceColorChannel.Skin));
        Assert.Equal(Vector4.One, f.Values[AppearanceColorChannel.Hair]);
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index); f.Tick();
        Assert.Single(f.Results);
        Assert.Equal(1, f.Releases);
    }

    [Fact]
    public void Inline_event_is_registered_but_cannot_commit_until_later_pumps()
    {
        var f = new Fixture { ImmediateEvent = true }; f.Begin();
        Assert.Empty(f.Results);
        f.Tick(); Assert.Empty(f.Results);
        f.Tick(); Assert.True(Assert.Single(f.Results).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeout_or_request_refusal_reenforces_current_owned_values_only(bool refuseRequest)
    {
        var f = new Fixture { RefuseRequest = refuseRequest }; f.Begin();
        if (!refuseRequest) { f.Now = 5000; f.Tick(); }
        Assert.False(Assert.Single(f.Results).Success);
        f.Values.Remove(AppearanceColorChannel.Hair);
        f.Values[AppearanceColorChannel.Skin] = new Vector4(0.2f);
        f.Revision++;
        f.Writes.Clear(); f.Tick();
        Assert.Equal([AppearanceColorChannel.Skin], f.Writes);
        Assert.Equal(new Vector4(0.2f), f.Native[AppearanceColorChannel.Skin]);
        Assert.False(f.Coordinator.IsPending(f.Actor));
    }

    [Fact]
    public void Foreign_hold_between_request_and_event_never_falls_back_to_native_writes()
    {
        var f = new Fixture(); f.Begin();
        f.Editable = false;
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index);
        f.Tick(); f.Tick();
        Assert.Empty(f.Writes);
        Assert.False(Assert.Single(f.Results).Success);
        Assert.Equal(0, f.Releases);
        f.Editable = true; f.Tick();
        Assert.Contains(AppearanceColorChannel.Skin, f.Writes);
        Assert.Contains(AppearanceColorChannel.Hair, f.Writes);
        f.Writes.Clear(); f.Begin();
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index); f.Tick(); f.Tick();
        Assert.True(f.Results[1].Success);
    }

    [Fact]
    public void Replacement_generation_cannot_receive_old_intent_even_with_reused_address_and_index()
    {
        var f = new Fixture(); f.Begin();
        f.LiveActor = new ActorId(f.Actor.LogicalId, f.Actor.Generation + 1);
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index); f.Tick(); f.Tick();
        Assert.Empty(f.Writes);
        Assert.Equal(0, f.Releases);
        Assert.False(Assert.Single(f.Results).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_or_disposal_invalidates_pending_before_new_intent(bool dispose)
    {
        var f = new Fixture(); f.Begin();
        f.Suspended = true; f.Revision++;
        if (dispose) f.Coordinator.Dispose(); else f.Coordinator.Cancel(f.Actor);
        f.Values[AppearanceColorChannel.Skin] = new Vector4(0.75f);
        f.Suspended = false; f.Revision++;
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index); f.Tick(); f.Tick();
        Assert.Equal(0, f.Releases);
        Assert.False(Assert.Single(f.Results).Success);
        if (dispose) Assert.Empty(f.Writes);
        else Assert.Equal(new Vector4(0.75f), f.Native[AppearanceColorChannel.Skin]);
    }

    [Fact]
    public void Refused_commit_retains_intent_and_inspects_once_per_actor_operation()
    {
        var f = new Fixture { RefuseCommit = true }; f.Begin();
        f.Coordinator.Redrawn(f.Target.Address, f.Target.Index);
        int probes = f.Probes;
        f.Tick(); Assert.Equal(probes + 1, f.Probes);
        f.Tick(); Assert.Equal(probes + 2, f.Probes);
        Assert.False(Assert.Single(f.Results).Success);
        Assert.Equal(0, f.Releases);
        Assert.Equal(Vector4.Zero, f.Native[AppearanceColorChannel.Skin]);
        f.Values.Clear(); probes = f.Probes; f.Tick();
        Assert.Equal(probes, f.Probes);
    }

    [Fact]
    public void Request_exception_completes_once_without_native_fallback()
    {
        var f = new Fixture { ThrowRequest = true }; f.Begin();
        Assert.False(Assert.Single(f.Results).Success);
        Assert.Empty(f.Writes);
        f.Coordinator.Dispose(); Assert.Single(f.Results);
    }

    private sealed class Fixture
    {
        public readonly ActorId Actor = new(Guid.NewGuid(), 1);
        public ActorId LiveActor;
        public readonly ColorTarget Target = new(123, 7);
        public readonly Dictionary<AppearanceColorChannel, Vector4> Values = new()
        { [AppearanceColorChannel.Skin] = Vector4.Zero, [AppearanceColorChannel.Hair] = Vector4.One };
        public readonly Dictionary<AppearanceColorChannel, Vector4> Native = new();
        public readonly List<AppearanceColorChannel> Writes = new();
        public readonly List<PresentationPortResult> Results = new();
        public ulong Revision;
        public bool Suspended, RefuseRequest, ImmediateEvent, RefuseCommit, ThrowRequest;
        public bool Readable = true, Editable = true;
        public long Now;
        public int Releases, Probes;
        public readonly ColorReleaseCoordinator Coordinator;
        public Fixture()
        {
            LiveActor = Actor;
            Coordinator = new(_ => new(Revision, Suspended, Values),
                actor => actor == LiveActor ? Target : null,
                actor => { Probes++; return new(actor == LiveActor ? Target : null, Editable, Readable, "foreign hold"); },
                _ =>
                {
                    if (ThrowRequest) throw new InvalidOperationException("request exception");
                    if (ImmediateEvent) Coordinator!.Redrawn(Target.Address, Target.Index);
                    return RefuseRequest ? PresentationPortResult.Fail("request refused") : PresentationPortResult.Ok();
                },
                (_, channel) => { Releases++; Values.Remove(channel); Revision++; },
                (_, _, values) => { foreach (var (channel, value) in values) { Writes.Add(channel); Native[channel] = value; } },
                () => Now);
        }
        public void Begin() => Coordinator.Begin(Actor, AppearanceColorChannel.Skin,
            mutation =>
            {
                if (RefuseCommit) return PresentationPortResult.Fail("history changed");
                mutation(); return PresentationPortResult.Ok();
            }, Results.Add);
        public void Tick() { Coordinator.AdvanceFrame(); Coordinator.Tick(Actor); }
    }
}
