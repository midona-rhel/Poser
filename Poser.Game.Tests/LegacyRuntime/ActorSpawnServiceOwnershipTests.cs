using Poser.Entities;
using Poser.Core;
using Poser.Game;
using Poser.Game.Types;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class ActorSpawnServiceOwnershipTests
{
    [Fact]
    public void Plain_companion_and_catalog_records_keep_their_own_metadata()
    {
        var ledger = new SpawnOwnershipLedger();
        var plain = ledger.Add(new(201, (nint)0x201, 11), CompanionKind.None, false);
        var companion = ledger.Add(new(202, (nint)0x202, 12), CompanionKind.None, true);
        var catalog = ledger.Add(new(203, (nint)0x203, 13), CompanionKind.Mount, false);

        Assert.False(plain.HasCompanionSlot);
        Assert.True(companion.HasCompanionSlot);
        Assert.Equal(CompanionKind.Mount, catalog.Kind);
        Assert.Equal(3, ledger.Snapshot.Count);
    }

    [Fact]
    public void Binding_requires_the_exact_actor_and_native_descriptor()
    {
        var ledger = new SpawnOwnershipLedger();
        var actor = Actor(0x301);
        var record = ledger.Add(new(301, actor.Address, 31), CompanionKind.None, false);

        Assert.True(ledger.Bind(record.Token, actor));
        Assert.True(ledger.TryGetExact(actor, record.Descriptor, out _));
        Assert.False(ledger.TryGetExact(actor, new(301, actor.Address, 32), out _));
        Assert.False(ledger.TryGetExact(Actor(0x302), record.Descriptor, out _));
    }

    [Fact]
    public void Index_reuse_is_fail_closed_and_does_not_inherit_metadata()
    {
        var ledger = new SpawnOwnershipLedger();
        var oldActor = Actor(0x401);
        var old = ledger.Add(new(401, oldActor.Address, 41), CompanionKind.Ornament, true);
        Assert.True(ledger.Bind(old.Token, oldActor));

        var replacement = new SpawnNativeDescriptor(401, (nint)0x402, 42);

        Assert.False(ledger.TryGetExact(oldActor, replacement, out _));
        Assert.Equal(CompanionKind.None, ledger.GetKind(oldActor, replacement));
        Assert.False(ledger.TryRetire(old.Token, replacement));
        Assert.Single(ledger.Snapshot);
    }

    [Fact]
    public void Delete_false_or_throw_leaves_a_retryable_pending_record()
    {
        var ledger = NewBoundLedger(out var record, out var actor);

        ledger.MarkPending(record.Token);
        Assert.Equal(SpawnOwnershipState.PendingDelete, record.State);
        Assert.True(ledger.TryGetExact(actor, record.Descriptor, out var pending));
        Assert.Equal(SpawnOwnershipState.PendingDelete, pending.State);

        Assert.Throws<InvalidOperationException>(ThrowNativeDelete);
        Assert.True(ledger.TryRetire(record.Token, record.Descriptor));
        Assert.Empty(ledger.Snapshot);
    }

    [Fact]
    public void Every_post_create_failure_stage_uses_the_same_exact_rollback_record()
    {
        string[] stages =
        [
            "resolve", "name", "appearance", "model", "position",
            "gpose", "draw", "refresh", "bind"
        ];

        foreach (var stage in stages)
        {
            var ledger = new SpawnOwnershipLedger();
            var record = ledger.Add(
                new(550, (nint)0x550, 55),
                stage == "model" ? CompanionKind.Mount : CompanionKind.None,
                stage == "gpose");
            ledger.MarkPending(record.Token);
            var native = new FakeNative(record.Descriptor);

            Assert.True(
                SpawnOwnershipCleanup.TryDelete(ledger, native, record),
                stage);
            Assert.Empty(ledger.Snapshot);
        }
    }

    [Fact]
    public void Delete_false_throw_manager_unavailable_and_reuse_mismatch_all_retain_pending()
    {
        var ledger = new SpawnOwnershipLedger();
        var record = ledger.Add(new(560, (nint)0x560, 56), CompanionKind.None, false);
        var native = new FakeNative(record.Descriptor);

        native.DeleteResult = false;
        Assert.False(SpawnOwnershipCleanup.TryDelete(ledger, native, record));
        Assert.Equal(SpawnOwnershipState.PendingDelete, record.State);

        native.ThrowOnDelete = true;
        Assert.False(SpawnOwnershipCleanup.TryDelete(ledger, native, record));

        native.ThrowOnDelete = false;
        native.IsAvailableValue = false;
        Assert.False(SpawnOwnershipCleanup.TryDelete(ledger, native, record));

        native.IsAvailableValue = true;
        native.Current = new SpawnNativeDescriptor(560, (nint)0x561, 57);
        Assert.False(SpawnOwnershipCleanup.TryDelete(ledger, native, record));
        Assert.Single(ledger.Snapshot);

        native.Current = record.Descriptor;
        native.DeleteResult = true;
        Assert.True(SpawnOwnershipCleanup.TryDelete(ledger, native, record));
        Assert.Empty(ledger.Snapshot);
    }

    [Fact]
    public void Unresolved_manager_does_not_clear_ownership_and_bulk_cleanup_is_partial()
    {
        var ledger = new SpawnOwnershipLedger();
        var first = ledger.Add(new(501, (nint)0x501, 51), CompanionKind.None, false);
        var second = ledger.Add(new(502, (nint)0x502, 52), CompanionKind.None, false);

        ledger.MarkPending(first.Token);
        var snapshot = ledger.Snapshot;
        Assert.Equal(2, snapshot.Count);

        Assert.False(ledger.TryRetire(first.Token, null));
        Assert.True(ledger.TryRetire(second.Token, second.Descriptor));
        Assert.Single(ledger.Snapshot);
        Assert.Equal(first.Token, ledger.Snapshot[0].Token);
    }

    [Fact]
    public void Visibility_is_metadata_on_the_exact_record_not_an_index_dictionary()
    {
        var ledger = new SpawnOwnershipLedger();
        var actor = Actor(0x601);
        var record = ledger.Add(new(601, actor.Address, 61), CompanionKind.None, false);
        Assert.True(ledger.Bind(record.Token, actor));

        Assert.True(ledger.TrySetVisibility(actor, record.Descriptor, false));
        Assert.False(record.Visible);
        Assert.False(ledger.TrySetVisibility(actor, new(601, actor.Address, 62), true));
        Assert.False(record.Visible);
    }

    private static SpawnOwnershipLedger NewBoundLedger(
        out SpawnOwnershipRecord record,
        out IActor actor)
    {
        var ledger = new SpawnOwnershipLedger();
        actor = Actor(0x701);
        record = ledger.Add(new(701, actor.Address, 71), CompanionKind.None, false);
        Assert.True(ledger.Bind(record.Token, actor));
        return ledger;
    }

    private static IActor Actor(nint address) =>
        new ActorBase(new EntityId($"test-{address}"), "Test", address);

    private static void ThrowNativeDelete() =>
        throw new InvalidOperationException("native delete");

    private sealed class FakeNative : IActorSpawnNativeAdapter
    {
        public FakeNative(SpawnNativeDescriptor descriptor) => Current = descriptor;
        public bool IsAvailableValue { get; set; } = true;
        public bool DeleteResult { get; set; } = true;
        public bool ThrowOnDelete { get; set; }
        public SpawnNativeDescriptor? Current { get; set; }
        public bool IsAvailable => IsAvailableValue;
        public uint CreateBattleCharacter(byte reserveCompanionSlot) => Current?.Index ?? 0xFFFFFFFF;
        public SpawnNativeDescriptor? ResolveByIndex(ushort index) =>
            Current is { } descriptor && descriptor.Index == index ? descriptor : null;
        public SpawnNativeDescriptor? ResolveActor(nint address) =>
            Current is { } descriptor && descriptor.Address == address ? descriptor : null;
        public bool DeleteExact(SpawnNativeDescriptor descriptor)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("native delete");
            return DeleteResult;
        }
    }
}
