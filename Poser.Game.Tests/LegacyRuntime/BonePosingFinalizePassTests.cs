namespace Poser.Game.Tests.LegacyRuntime;

/// <summary>
/// Characterizes the collection discipline of BonePosingService's finalize
/// pass (FinalizeSkeletonsDetour). The service itself constructs native hooks
/// in its constructor, so the discipline is characterized directly: the pass
/// iterates skeleton-key sets whose visit step (UpdateSkeletonCache →
/// GetSkeleton) can synchronously publish SkeletonChangedEvent, whose handler
/// mutates BOTH sets (PurgeSkeletonState removes; the replacement path
/// re-adds). The pass must therefore iterate snapshots, and the overlay-only
/// dedupe must test the snapshot, not the live set.
/// </summary>
public sealed class BonePosingFinalizePassTests
{
    private readonly record struct Key(nint Actor, int Slot, string Skeleton);

    [Fact]
    public void Live_set_enumeration_throws_when_the_visit_mutates_the_set()
    {
        // The pre-fix shape: foreach over the live set while the visit's
        // synchronous event handler purges the old key AND registers the
        // replacement (OnSkeletonChanged: PurgeSkeletonState + Add(newKey)).
        // Remove alone is tolerated by modern HashSet enumerators; the Add is
        // what throws — an InvalidOperationException inside the
        // FinalizeSkeletons frame.
        var live = new HashSet<Key> { new(1, 0, "a"), new(2, 0, "b") };

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var key in live)
            {
                live.Remove(key); // handler: PurgeSkeletonState
                live.Add(key with { Skeleton = key.Skeleton + "-replaced" });
            }
        });
    }

    [Fact]
    public void Snapshot_pass_survives_purge_and_readd_during_the_visit()
    {
        // The fixed shape: both sets are copied into reused buffers before
        // the loops; the visit may freely mutate the live sets.
        var toUpdate = new HashSet<Key> { new(1, 0, "a"), new(2, 0, "b") };
        var toUpdateCache = new HashSet<Key> { new(2, 0, "b"), new(3, 0, "c") };

        var passBuffer = new List<Key>();
        var cachePassBuffer = new List<Key>();
        passBuffer.Clear();
        foreach (var key in toUpdate)
            passBuffer.Add(key);
        cachePassBuffer.Clear();
        foreach (var key in toUpdateCache)
            cachePassBuffer.Add(key);

        var visited = new List<Key>();
        void Visit(Key key)
        {
            visited.Add(key);
            // Handler for a replaced slot: purge the old instance from both
            // sets and register the replacement in the update set.
            toUpdate.Remove(key);
            toUpdateCache.Remove(key);
            toUpdate.Add(key with { Skeleton = key.Skeleton + "-replaced" });
        }

        for (var i = 0; i < passBuffer.Count; i++)
            Visit(passBuffer[i]);
        for (var i = 0; i < cachePassBuffer.Count; i++)
        {
            if (!passBuffer.Contains(cachePassBuffer[i]))
                Visit(cachePassBuffer[i]);
        }

        // Every snapshotted key visited exactly once; the overlay-only key
        // came through the cache pass; the modified key shared by both sets
        // was not visited twice.
        Assert.Equal(
            new[] { new Key(1, 0, "a"), new Key(2, 0, "b"), new Key(3, 0, "c") },
            visited.OrderBy(k => k.Actor).ToArray());
    }

    [Fact]
    public void Dedupe_against_the_snapshot_not_the_live_set()
    {
        // A key the handler ADDS to the update set during the first loop was
        // never updated by that loop. Deduping against the live set would
        // skip it; deduping against the snapshot visits it.
        var toUpdate = new HashSet<Key> { new(1, 0, "a") };
        var overlayOnly = new Key(2, 0, "b");
        var toUpdateCache = new HashSet<Key> { overlayOnly };

        var passBuffer = new List<Key>(toUpdate);
        var cachePassBuffer = new List<Key>(toUpdateCache);

        var visited = new List<Key>();
        for (var i = 0; i < passBuffer.Count; i++)
        {
            visited.Add(passBuffer[i]);
            toUpdate.Add(overlayOnly); // handler re-adds mid-pass
        }

        for (var i = 0; i < cachePassBuffer.Count; i++)
        {
            if (!passBuffer.Contains(cachePassBuffer[i]))
                visited.Add(cachePassBuffer[i]);
        }

        Assert.Contains(overlayOnly, visited);
    }
}
