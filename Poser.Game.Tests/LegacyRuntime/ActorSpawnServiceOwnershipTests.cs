using Poser.Entities;
using Poser.Core;
using Poser.Game;
using Poser.Game.Types;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class ActorSpawnServiceOwnershipTests
{
    [Fact]
    public void Public_spawn_and_catalog_paths_bind_only_after_exact_refresh_identity()
    {
        var actor = Actor(0x810);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(810, actor.Address, 810));
        using var service = NewService(native, manager, (_, _, _, _) => { });

        var plain = service.SpawnNewActor(reserveCompanionSlot: true);
        Assert.Same(actor, plain);
        Assert.True(service.IsSpawnedActor(actor));
        Assert.True(Assert.Single(service.OwnershipSnapshot).HasCompanionSlot);

        native.Current = new(811, (nint)0x811, 811);
        var catalogActor = Actor(native.Current.Value.Address);
        manager.Actors = [catalogActor];
        var catalog = service.SpawnCatalogActor(
            new SpawnCatalogEntry(
                CompanionKind.Mount,
                9,
                "Test mount",
                "test mount",
                0,
                123));

        Assert.Same(catalogActor, catalog);
        Assert.Equal(CompanionKind.Mount, service.GetSpawnedKind(catalogActor));
        Assert.Equal(CompanionKind.None, service.GetSpawnedKind(actor));
        Assert.False(service.IsSpawnedActor(actor));
    }

    [Fact]
    public void Public_spawn_rejects_replacement_seen_after_refresh_and_retains_pending_delete()
    {
        var actor = Actor(0x820);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(820, actor.Address, 820));
        manager.RefreshAction = () => native.Current = new(820, (nint)0x821, 821);
        using var service = NewService(native, manager, (_, _, _, _) => { });

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingDelete, pending.State);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void Public_spawn_rolls_back_each_injected_post_create_phase_failure()
    {
        string[] phases = ["name", "appearance", "model", "position", "gpose", "draw"];
        foreach (var phase in phases)
        {
            var offset = Array.IndexOf(phases, phase);
            var actor = Actor((nint)(0x830 + offset));
            var manager = new FakeActorManager(actor);
            var native = new FakeNative(new(
                (ushort)(830 + offset),
                actor.Address,
                (ulong)(830 + offset)));
            using var service = NewService(
                native,
                manager,
                (_, _, _, _) => throw new InvalidOperationException(phase));

            Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
            Assert.Empty(service.OwnershipSnapshot);
            Assert.Single(native.Deleted);
        }
    }

    [Fact]
    public void Owned_callbacks_no_op_after_retirement_reuse_or_same_descriptor_aba()
    {
        var actor = Actor(0x840);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(840, actor.Address, 840));
        using var service = NewService(native, manager, (_, _, _, _) => { });

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        var record = Assert.Single(service.OwnershipSnapshot);
        var ran = false;
        Assert.True(service.InvokeOwnedCallbackForTests(
            record.Token,
            record.Descriptor!.Value,
            () => ran = true));
        Assert.True(ran);

        Assert.True(service.DestroyActor(actor));
        ran = false;
        Assert.False(service.InvokeOwnedCallbackForTests(
            record.Token,
            record.Descriptor!.Value,
            () => ran = true));
        Assert.False(ran);

        native.Current = new(840, actor.Address, 840, 1);
        var replacement = Actor(actor.Address);
        manager.Actors = [replacement];
        Assert.Same(replacement, service.SpawnNewActor(reserveCompanionSlot: false));
        var replacementRecord = Assert.Single(service.OwnershipSnapshot);
        ran = false;
        Assert.False(service.InvokeOwnedCallbackForTests(
            replacementRecord.Token,
            replacementRecord.Descriptor!.Value with { Generation = 2 },
            () => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void Public_destroy_is_retryable_and_manager_unavailability_never_clears_ownership()
    {
        var actor = Actor(0x850);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(850, actor.Address, 850));
        using var service = NewService(native, manager, (_, _, _, _) => { });

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        native.DeleteResult = false;
        Assert.False(service.DestroyActor(actor));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingDelete, pending.State);

        native.IsAvailableValue = false;
        Assert.False(service.DestroyActor(actor));
        Assert.Single(service.OwnershipSnapshot);

        native.IsAvailableValue = true;
        native.DeleteResult = true;
        Assert.True(service.DestroyActor(actor));
        Assert.Empty(service.OwnershipSnapshot);
    }

    [Fact]
    public void Public_spawn_keeps_a_pending_create_when_resolve_returns_null()
    {
        var native = new FakeNative(new(801, (nint)0x801, 81))
        {
            ResolveReturnsNull = true,
        };
        using var service = NewService(native);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingCreate, pending.State);
        Assert.Equal((ushort)801, pending.CreatedIndex);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void Public_spawn_keeps_an_unresolved_record_when_create_throws()
    {
        var native = new FakeNative(new(802, (nint)0x802, 82))
        {
            ThrowOnCreate = true,
        };
        using var service = NewService(native);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: true));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingCreate, pending.State);
        Assert.Equal(ushort.MaxValue, pending.CreatedIndex);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void Public_spawn_keeps_exact_rollback_evidence_when_identity_resolution_throws()
    {
        var native = new FakeNative(new(803, (nint)0x803, 83))
        {
            ThrowOnResolve = true,
        };
        using var service = NewService(native);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal((ushort)803, pending.CreatedIndex);
        Assert.Equal(SpawnOwnershipState.PendingCreate, pending.State);
    }

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
        Assert.True(ledger.TryGetExact(actor, record.Descriptor!.Value, out _));
        Assert.False(ledger.TryGetExact(actor, new(301, actor.Address, 32), out _));
        Assert.False(ledger.TryGetExact(Actor(0x302), record.Descriptor!.Value, out _));
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
        Assert.False(ledger.TryGetExact(actor, record.Descriptor!.Value, out _));
        Assert.Equal(SpawnOwnershipState.PendingDelete, record.State);

        Assert.Throws<InvalidOperationException>(ThrowNativeDelete);
        Assert.True(ledger.TryRetire(record.Token, record.Descriptor!.Value));
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
            var native = new FakeNative(record.Descriptor!.Value);

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
        var native = new FakeNative(record.Descriptor!.Value);

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

        native.Current = record.Descriptor!.Value;
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

        Assert.True(ledger.TrySetVisibility(actor, record.Descriptor!.Value, false));
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

    private static ActorSpawnService NewService(
        FakeNative native,
        FakeActorManager? manager = null,
        Action<SpawnOwnershipRecord, nint, int, string?>? mutate = null) =>
        new(
            new FakeGPoseService(),
            manager ?? new FakeActorManager(),
            new FakeEventBus(),
            native,
            () => (nint)0x100,
            null,
            null,
            mutate);

    private static void ThrowNativeDelete() =>
        throw new InvalidOperationException("native delete");

    private sealed class FakeNative : IActorSpawnNativeAdapter
    {
        public FakeNative(SpawnNativeDescriptor descriptor) => Current = descriptor;
        public bool IsAvailableValue { get; set; } = true;
        public bool DeleteResult { get; set; } = true;
        public bool ThrowOnDelete { get; set; }
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnResolve { get; set; }
        public bool ResolveReturnsNull { get; set; }
        public List<SpawnNativeDescriptor> Deleted { get; } = new();
        public SpawnNativeDescriptor? Current { get; set; }
        public bool IsAvailable => IsAvailableValue;
        public uint CreateBattleCharacter(byte reserveCompanionSlot)
        {
            if (ThrowOnCreate)
                throw new InvalidOperationException("create");
            return Current?.Index ?? 0xFFFFFFFF;
        }
        public SpawnNativeDescriptor? ResolveByIndex(ushort index) =>
            ThrowOnResolve
                ? throw new InvalidOperationException("resolve")
                : ResolveReturnsNull
                    ? null
                    : Current is { } descriptor && descriptor.Index == index ? descriptor : null;
        public SpawnNativeDescriptor? ResolveActor(nint address) =>
            Current is { } descriptor && descriptor.Address == address ? descriptor : null;
        public bool DeleteExact(SpawnNativeDescriptor descriptor)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("native delete");
            if (DeleteResult)
                Deleted.Add(descriptor);
            return DeleteResult;
        }
    }

    private sealed class FakeActorManager : IActorManager
    {
        public FakeActorManager(IActor? actor = null) =>
            Actors = actor is null ? Array.Empty<IActor>() : [actor];
        public IReadOnlyList<IActor> Actors { get; set; }
        public IReadOnlyList<IActor> AuxiliaryActors { get; } = Array.Empty<IActor>();
        public Action? RefreshAction { get; set; }
        public void Dispose() { }
        public void RegisterAuxiliary(ushort objectIndex, ActorKind kind) { }
        public void UnregisterAuxiliary(ushort objectIndex) { }
        public void RefreshActors() => RefreshAction?.Invoke();
        public IActor? GetGPoseTarget() => null;
        public void SetGPoseTarget(IActor actor) { }
    }

    private sealed class FakeGPoseService : IGPoseService
    {
        public bool IsGPosing => false;
        public void Dispose() { }
        public void ExitForUnload() { }
    }

    private sealed class FakeEventBus : IEventBus
    {
        public void Dispose() { }
        public void Subscribe<T>(Action<T> handler) where T : IEvent { }
        public void Unsubscribe<T>(Action<T> handler) where T : IEvent { }
        public void Publish<T>(T evt) where T : IEvent { }
    }
}
