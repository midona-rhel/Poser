using System;
using System.Collections.Generic;

namespace Poser.Game.WorldObjects;

internal enum VfxAllocationMatch
{
    Exact,
    Vanished,
    Replaced,
    Ambiguous,
}

internal readonly record struct VfxAllocationLease(
    WorldObjectIncarnation Identity,
    IDisposable Claim);

internal readonly record struct VfxCurrentObservation(
    bool Exists,
    bool IsVfx,
    nint ResourceIdentity);

/// <summary>Pure ownership state for native VFX allocations. A pending lease
/// is keyed by its immutable generation, never by address; only its explicit
/// synchronous promotion may turn zero resource identity into a live claim.
/// </summary>
internal sealed class VfxOwnedAllocationLedger
{
    private sealed class Entry
    {
        public Entry(WorldObjectIncarnation identity, IDisposable claim)
        {
            Identity = identity;
            Claim = claim;
        }

        public WorldObjectIncarnation Identity;
        public IDisposable Claim { get; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<WorldObjectIncarnation, Entry> _pending = new();
    private readonly Dictionary<WorldObjectIncarnation, Entry> _live = new();
    private readonly Dictionary<nint, WorldObjectIncarnation> _observed = new();
    private long _nextGeneration;

    public VfxAllocationLease Reserve(nint address, IDisposable claim)
    {
        lock (_gate)
        {
            var identity = new WorldObjectIncarnation(
                address, ++_nextGeneration, nint.Zero, true);
            _pending.Add(identity, new Entry(identity, claim));
            _observed[address] = identity;
            return new VfxAllocationLease(identity, claim);
        }
    }

    public bool TryPromote(
        VfxAllocationLease lease,
        nint resource,
        out WorldObjectIncarnation identity)
    {
        lock (_gate)
        {
            identity = default;
            if (resource == nint.Zero
                || !_pending.Remove(lease.Identity, out var entry))
                return false;
            identity = lease.Identity with { ResourceIdentity = resource };
            entry.Identity = identity;
            _live.Add(identity, entry);
            _observed[identity.Address] = identity;
            return true;
        }
    }

    /// <summary>General reads never promote pending state. They return the
    /// pending identity only for the still-zero observation; a later resource
    /// is represented as a stable replacement identity until promotion.</summary>
    public WorldObjectIncarnation Observe(nint address, nint resource)
    {
        lock (_gate)
        {
            if (_observed.TryGetValue(address, out var prior)
                && prior.ResourceIdentity == resource)
                return prior;
            var identity = new WorldObjectIncarnation(
                address, ++_nextGeneration, resource, true);
            _observed[address] = identity;
            return identity;
        }
    }

    public VfxAllocationMatch Match(
        WorldObjectIncarnation identity,
        nint currentResource,
        bool nativeExists) =>
        Match(identity, new VfxCurrentObservation(
            nativeExists, true, currentResource));

    public VfxAllocationMatch Match(
        WorldObjectIncarnation identity,
        VfxCurrentObservation current)
    {
        lock (_gate)
        {
            if (!current.Exists)
                return VfxAllocationMatch.Vanished;
            if (!current.IsVfx)
                return VfxAllocationMatch.Replaced;
            if (!_pending.ContainsKey(identity) && !_live.ContainsKey(identity))
                return VfxAllocationMatch.Replaced;
            if (identity.ResourceIdentity != nint.Zero
                && current.ResourceIdentity == nint.Zero)
                return VfxAllocationMatch.Ambiguous;
            if (identity.ResourceIdentity != nint.Zero
                && identity.ResourceIdentity != current.ResourceIdentity)
                return VfxAllocationMatch.Replaced;
            return identity.ResourceIdentity != nint.Zero
                ? VfxAllocationMatch.Exact
                : VfxAllocationMatch.Ambiguous;
        }
    }

    /// <summary>Stale-release policy used when the native object is no
    /// longer the caller's exact live instance. A live exact or ambiguous
    /// observation must remain owned until exact teardown succeeds.</summary>
    public bool TryReleaseIfVanishedOrReplaced(
        WorldObjectIncarnation identity,
        nint currentResource,
        bool nativeExists) =>
        TryReleaseIfVanishedOrReplaced(identity, new VfxCurrentObservation(
            nativeExists, true, currentResource));

    public bool TryReleaseIfVanishedOrReplaced(
        WorldObjectIncarnation identity,
        VfxCurrentObservation current)
    {
        var match = Match(identity, current);
        return (match is VfxAllocationMatch.Vanished
            or VfxAllocationMatch.Replaced)
            && Release(identity);
    }

    public bool Release(WorldObjectIncarnation identity)
    {
        lock (_gate)
        {
            if (_pending.Remove(identity, out var pending))
            {
                pending.Claim.Dispose();
                RetireObserved(identity);
                return true;
            }
            if (_live.Remove(identity, out var live))
            {
                live.Claim.Dispose();
                RetireObserved(identity);
            }
            return true;
        }
    }

    public bool TryGetPending(nint address, out VfxAllocationLease lease)
    {
        lock (_gate)
        {
            foreach (var entry in _pending.Values)
                if (entry.Identity.Address == address)
                {
                    lease = new VfxAllocationLease(
                        entry.Identity, entry.Claim);
                    return true;
                }
            lease = default;
            return false;
        }
    }

    public WorldObjectIncarnation[] LiveIdentities
    {
        get
        {
            lock (_gate)
            {
                var result = new WorldObjectIncarnation[_live.Count];
                int i = 0;
                foreach (var entry in _live.Values)
                    result[i++] = entry.Identity;
                return result;
            }
        }
    }

    public VfxAllocationLease[] PendingLeases
    {
        get
        {
            lock (_gate)
            {
                var result = new VfxAllocationLease[_pending.Count];
                int i = 0;
                foreach (var entry in _pending.Values)
                    result[i++] = new VfxAllocationLease(
                        entry.Identity, entry.Claim);
                return result;
            }
        }
    }

    public bool HasClaims
    {
        get
        {
            lock (_gate)
                return _pending.Count != 0 || _live.Count != 0;
        }
    }

    private void RetireObserved(WorldObjectIncarnation identity)
    {
        if (_observed.TryGetValue(identity.Address, out var observed)
            && observed == identity)
            _observed.Remove(identity.Address);
    }
}
