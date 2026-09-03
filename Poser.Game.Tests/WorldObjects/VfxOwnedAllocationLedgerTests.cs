using Poser.Game.WorldObjects;

namespace Poser.Game.Tests.WorldObjects;

public sealed class VfxOwnedAllocationLedgerTests
{
    [Fact]
    public void Promotion_keeps_reserved_generation_and_generic_read_does_not_promote()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var token = new CountingDisposable();
        var lease = ledger.Reserve((nint)0x1000, token);

        var observed = ledger.Observe((nint)0x1000, (nint)0x2000);
        Assert.NotEqual(lease.Identity.Generation, observed.Generation);
        Assert.Equal(VfxAllocationMatch.Ambiguous,
            ledger.Match(lease.Identity, (nint)0x2000, true));

        Assert.True(ledger.TryPromote(
            lease, (nint)0x2000, out var live));
        Assert.Equal(lease.Identity.Generation, live.Generation);
        Assert.Equal((nint)0x2000, live.ResourceIdentity);
    }

    [Fact]
    public void Newer_pending_lease_never_resolves_to_old_live_generation()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var oldToken = new CountingDisposable();
        using var newToken = new CountingDisposable();
        var oldLease = ledger.Reserve((nint)0x1000, oldToken);
        Assert.True(ledger.TryPromote(oldLease, (nint)0x2000, out var oldLive));
        var newLease = ledger.Reserve((nint)0x1000, newToken);

        var first = ledger.Observe((nint)0x1000, (nint)0x2000);
        var repeated = ledger.Observe((nint)0x1000, (nint)0x2000);
        Assert.NotEqual(oldLive.Generation, first.Generation);
        Assert.Equal(first, repeated);
        Assert.Equal(VfxAllocationMatch.Ambiguous,
            ledger.Match(newLease.Identity, (nint)0x2000, true));
    }

    [Fact]
    public void Missing_resource_is_ambiguous_but_nonzero_replacement_is_replaced()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var token = new CountingDisposable();
        var lease = ledger.Reserve((nint)0x1000, token);
        Assert.True(ledger.TryPromote(lease, (nint)0x2000, out var live));

        Assert.Equal(VfxAllocationMatch.Ambiguous,
            ledger.Match(live, nint.Zero, true));
        Assert.Equal(VfxAllocationMatch.Replaced,
            ledger.Match(live, (nint)0x3000, true));
    }

    [Fact]
    public void Stale_release_keeps_live_ambiguous_claim_until_replaced()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var token = new CountingDisposable();
        var lease = ledger.Reserve((nint)0x1000, token);
        Assert.True(ledger.TryPromote(lease, (nint)0x2000, out var live));

        Assert.False(ledger.TryReleaseIfVanishedOrReplaced(
            live, nint.Zero, nativeExists: true));
        Assert.True(ledger.HasClaims);

        Assert.True(ledger.TryReleaseIfVanishedOrReplaced(
            live, (nint)0x3000, nativeExists: true));
        Assert.False(ledger.HasClaims);
    }

    [Fact]
    public void Different_native_kind_is_replaced_but_same_kind_missing_resource_is_ambiguous()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var token = new CountingDisposable();
        var lease = ledger.Reserve((nint)0x1000, token);
        Assert.True(ledger.TryPromote(lease, (nint)0x2000, out var live));

        Assert.Equal(VfxAllocationMatch.Ambiguous, ledger.Match(
            live, new VfxCurrentObservation(true, true, nint.Zero)));
        Assert.Equal(VfxAllocationMatch.Replaced, ledger.Match(
            live, new VfxCurrentObservation(true, false, nint.Zero)));
        Assert.True(ledger.TryReleaseIfVanishedOrReplaced(
            live, new VfxCurrentObservation(true, false, nint.Zero)));
        Assert.False(ledger.HasClaims);
    }

    [Fact]
    public void Same_address_pending_leases_release_independently_and_idempotently()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var firstToken = new CountingDisposable();
        using var secondToken = new CountingDisposable();
        var first = ledger.Reserve((nint)0x1000, firstToken);
        var second = ledger.Reserve((nint)0x1000, secondToken);

        Assert.True(ledger.Release(first.Identity));
        Assert.True(ledger.Release(first.Identity));
        Assert.Equal(1, firstToken.Count);
        var remaining = Assert.Single(ledger.PendingLeases);
        Assert.Equal(second.Identity, remaining.Identity);
        Assert.True(ledger.Release(second.Identity));
        Assert.True(ledger.Release(second.Identity));
        Assert.Equal(1, secondToken.Count);
        Assert.False(ledger.HasClaims);
    }

    [Fact]
    public void Path_claim_counts_cover_two_live_same_path_instances()
    {
        var paths = new VfxPathClaimOwner();
        using var first = paths.Acquire("vfx/fire.avfx");
        using var second = paths.Acquire("VFX/FIRE.AVFX");
        Assert.Equal(2, paths.Count("vfx/fire.avfx"));
        first.Dispose();
        Assert.Equal(1, paths.Count("vfx/fire.avfx"));
        second.Dispose();
        Assert.Equal(0, paths.Count("vfx/fire.avfx"));
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }
}
