using Poser.Game.WorldObjects;

namespace Poser.Game.Tests.WorldObjects;

public sealed class VfxOwnedAllocationLedgerTests
{
    [Fact]
    public void Promotion_keeps_reserved_generation_and_generic_read_does_not_promote()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var token = new NullDisposable();
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
    public void Observation_classifies_ambiguous_vs_replaced_and_only_releases_replaced()
    {
        var ledger = new VfxOwnedAllocationLedger();
        using var token = new NullDisposable();
        var lease = ledger.Reserve((nint)0x1000, token);
        Assert.True(ledger.TryPromote(lease, (nint)0x2000, out var live));

        // Same kind with its resource missing may still be ours: keep the claim.
        var ambiguous = new VfxCurrentObservation(true, true, nint.Zero);
        Assert.Equal(VfxAllocationMatch.Ambiguous, ledger.Match(live, ambiguous));
        Assert.False(ledger.TryReleaseIfVanishedOrReplaced(live, ambiguous));
        Assert.True(ledger.HasClaims);

        // A different resource or native kind is someone else's object.
        Assert.Equal(VfxAllocationMatch.Replaced, ledger.Match(live, (nint)0x3000, true));
        var replaced = new VfxCurrentObservation(true, false, nint.Zero);
        Assert.Equal(VfxAllocationMatch.Replaced, ledger.Match(live, replaced));
        Assert.True(ledger.TryReleaseIfVanishedOrReplaced(live, replaced));
        Assert.False(ledger.HasClaims);
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
