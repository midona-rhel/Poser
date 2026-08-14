using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Game;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class ActorSpawnServiceOwnershipTests
{
    [Fact]
    public void Lifetime_stamps_advance_only_at_destruction_and_stay_per_address_and_index()
    {
        var stamps = new SpawnLifetimeStamps();
        Assert.Equal(0UL, stamps.StampFor((nint)0x1));
        Assert.Equal(0UL, stamps.IndexStampFor(201));

        stamps.NoteDestroyed((nint)0x1, 201);
        Assert.Equal(1UL, stamps.StampFor((nint)0x1));
        Assert.Equal(1UL, stamps.IndexStampFor(201));

        stamps.NoteDestroyed((nint)0x2, null);
        Assert.Equal(2UL, stamps.StampFor((nint)0x2));
        Assert.Equal(1UL, stamps.StampFor((nint)0x1));

        stamps.NoteDestroyed((nint)0x1, 201);
        Assert.Equal(3UL, stamps.StampFor((nint)0x1));
        Assert.Equal(3UL, stamps.IndexStampFor(201));
    }

    [Fact]
    public void Overflow_clear_never_lets_post_clear_stamps_match_pre_clear_descriptors()
    {
        var stamps = new SpawnLifetimeStamps();

        // Pre-clear epoch descriptors: a never-destroyed address carries the
        // implicit stamp 0, a destroyed one carries its sequence value.
        var untouched = (nint)0xA000;
        var destroyed = (nint)0xB000;
        var untouchedStamp = stamps.StampFor(untouched);
        Assert.Equal(0UL, untouchedStamp);
        stamps.NoteDestroyed(destroyed, 250);
        var destroyedStamp = stamps.StampFor(destroyed);
        var destroyedIndexStamp = stamps.IndexStampFor(250);

        // Drive the map past the overflow cap with distinct addresses so the
        // clear path runs.
        for (var i = 0; i < 8192; i++)
            stamps.NoteDestroyed((nint)(0x10_0000 + i), null);

        // Post-clear resolves sit at or above the clear floor, strictly above
        // every pre-clear stamp: a stored pre-clear descriptor can never
        // compare equal again — including the stamp-0 one.
        Assert.True(stamps.StampFor(untouched) > untouchedStamp);
        Assert.True(stamps.StampFor(destroyed) > destroyedStamp);
        Assert.True(stamps.IndexStampFor(250) > destroyedIndexStamp);

        // Stamps keep advancing monotonically in the new epoch.
        var floor = stamps.StampFor(destroyed);
        stamps.NoteDestroyed(destroyed, 250);
        Assert.True(stamps.StampFor(destroyed) > floor);
    }

    [Fact]
    public void Clone_refuses_a_source_whose_identity_does_not_resolve()
    {
        var actor = Actor(0x990);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(990, actor.Address, 990));
        using var service = NewService(native, manager);

        // A wrapper whose remembered address resolves to no current native
        // object refuses before any native create or dereference.
        var stale = Actor(0x991);
        Assert.Null(service.CloneActor(stale));
        Assert.Equal(0, native.CreateCalls);
        Assert.Empty(service.OwnershipSnapshot);

        // A resolution fault is refusal, not permission.
        native.ThrowOnResolve = true;
        Assert.Null(service.CloneActor(actor));
        Assert.Equal(0, native.CreateCalls);
        Assert.Empty(service.OwnershipSnapshot);

        // Positive control: the same source clones once it resolves.
        native.ThrowOnResolve = false;
        Assert.Same(actor, service.CloneActor(actor));
        Assert.Equal(1, native.CreateCalls);
        Assert.True(service.IsSpawnedActor(actor));
    }

    [Fact]
    public void World_source_clone_runs_the_same_owned_spawn_transaction()
    {
        var actor = Actor(0x870);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(870, actor.Address, 870));
        using var service = NewService(native, manager);

        // The source address is an overworld object the COM-based resolver
        // could never prove; the caller (WorldActorDiscovery) proved it via
        // its own object-table observation on this same tick.
        var spawned = service.CloneFromWorldSource((nint)0x777);

        Assert.Same(actor, spawned);
        Assert.Equal(1, native.CreateCalls);
        Assert.True(service.IsSpawnedActor(actor));
        Assert.True(Assert.Single(service.OwnershipSnapshot).HasCompanionSlot);
    }

    [Fact]
    public void World_source_clone_refuses_all_spawn_gates_before_any_create()
    {
        var actor = Actor(0x871);
        var manager = new FakeActorManager(actor);

        // No address: refusal before the transaction.
        var native = new FakeNative(new(871, actor.Address, 871));
        using (var service = NewService(native, manager))
        {
            Assert.Null(service.CloneFromWorldSource(nint.Zero));
            Assert.Equal(0, native.CreateCalls);
        }

        // No authoritative lifetime transition: spawning refuses outright.
        var unhooked = new FakeNative(new(871, actor.Address, 871))
        {
            IsLifetimeAuthoritativeValue = false,
        };
        using (var service = NewService(unhooked, manager))
        {
            Assert.Null(service.CloneFromWorldSource((nint)0x777));
            Assert.Equal(0, unhooked.CreateCalls);
        }

        // Off the framework thread: refusal like every public operation.
        var framework = new FakeFramework { InThread = false };
        var offThread = new FakeNative(new(871, actor.Address, 871));
        using (var service = NewService(offThread, manager, framework: framework))
        {
            Assert.Null(service.CloneFromWorldSource((nint)0x777));
            Assert.Equal(0, offThread.CreateCalls);
        }
    }

    [Fact]
    public void World_source_clone_rejects_replacement_seen_after_refresh()
    {
        // Revalidation before BIND is the transaction's own: a replacement
        // observed after the actor-list refresh rolls the spawn back and the
        // world path inherits it unchanged.
        var actor = Actor(0x872);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(872, actor.Address, 872));
        manager.RefreshAction = () => native.Current = new(872, (nint)0x873, 873);
        using var service = NewService(native, manager);

        Assert.Null(service.CloneFromWorldSource((nint)0x777));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingDelete, pending.State);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void Public_spawn_and_catalog_paths_bind_only_after_exact_refresh_identity()
    {
        var actor = Actor(0x810);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(810, actor.Address, 810));
        using var service = NewService(native, manager);

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
        Assert.Null(service.GetSpawnedKind(actor));
        Assert.False(service.IsSpawnedActor(actor));
    }

    [Fact]
    public void Public_spawn_rejects_replacement_seen_after_refresh_and_retains_pending_delete()
    {
        var actor = Actor(0x820);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(820, actor.Address, 820));
        manager.RefreshAction = () => native.Current = new(820, (nint)0x821, 821);
        using var service = NewService(native, manager);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingDelete, pending.State);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void Public_spawn_rejects_a_wrapper_with_the_wrong_logical_identity_and_rolls_back()
    {
        var address = (nint)0x825;
        var wrongWrapper = new ActorBase(new EntityId("stale-wrapper"), "Test", address);
        var manager = new FakeActorManager(wrongWrapper);
        var native = new FakeNative(new(825, address, 825));
        using var service = NewService(native, manager);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.Empty(service.OwnershipSnapshot);
        Assert.Single(native.Deleted);
    }

    [Fact]
    public void Public_spawn_rolls_back_when_wrapper_identity_cannot_be_derived()
    {
        var actor = Actor(0x826);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(826, actor.Address, 826));
        using var service = NewService(native, manager, expectedIdentity: _ => null);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.Empty(service.OwnershipSnapshot);
        Assert.Single(native.Deleted);
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
    public void External_delete_with_identical_triple_reuse_fails_closed_everywhere()
    {
        var actor = Actor(0x900);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(900, actor.Address, 900));
        using var service = NewService(native, manager);

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        var record = Assert.Single(service.OwnershipSnapshot);
        Assert.True(service.IsSpawnedActor(actor));

        // External delete + reuse with the SAME index, address, and EntityId,
        // entirely between adapter observations. Only the finalize-hook
        // destruction stamp distinguishes the occupant.
        native.ExternallyDestroyCurrent();

        Assert.False(service.DestroyActor(actor));
        Assert.Empty(native.Deleted);
        Assert.False(service.IsSpawnedActor(actor));
        Assert.Null(service.GetSpawnedKind(actor));

        var ran = false;
        Assert.False(service.InvokeOwnedCallbackForTests(
            record.Token,
            record.Descriptor!.Value,
            () => ran = true));
        Assert.False(ran);

        // The record is retained (fail closed) rather than transferred to the
        // foreign occupant.
        Assert.Single(service.OwnershipSnapshot);
    }

    [Fact]
    public void Owned_callbacks_no_op_after_retirement_reuse_or_same_descriptor_aba()
    {
        var actor = Actor(0x840);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(840, actor.Address, 840));
        using var service = NewService(native, manager);

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

        native.Current = new(840, actor.Address, 840);
        var replacement = Actor(actor.Address);
        manager.Actors = [replacement];
        Assert.Same(replacement, service.SpawnNewActor(reserveCompanionSlot: false));
        var replacementRecord = Assert.Single(service.OwnershipSnapshot);
        ran = false;
        Assert.False(service.InvokeOwnedCallbackForTests(
            replacementRecord.Token,
            replacementRecord.Descriptor!.Value with
            {
                LifetimeStamp = replacementRecord.Descriptor!.Value.LifetimeStamp + 1,
            },
            () => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void Owned_callbacks_refuse_while_the_native_manager_is_unavailable()
    {
        var actor = Actor(0x845);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(845, actor.Address, 845));
        using var service = NewService(native, manager);

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        var record = Assert.Single(service.OwnershipSnapshot);

        native.IsAvailableValue = false;
        var ran = false;
        Assert.False(service.InvokeOwnedCallbackForTests(
            record.Token,
            record.Descriptor!.Value,
            () => ran = true));
        Assert.False(ran);
        Assert.Single(service.OwnershipSnapshot);

        native.IsAvailableValue = true;
        Assert.True(service.InvokeOwnedCallbackForTests(
            record.Token,
            record.Descriptor!.Value,
            () => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public void Public_destroy_is_retryable_and_manager_unavailability_never_clears_ownership()
    {
        var actor = Actor(0x850);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(850, actor.Address, 850));
        using var service = NewService(native, manager);

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
    public void Spawning_refuses_without_an_authoritative_lifetime_transition()
    {
        var actor = Actor(0x855);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(855, actor.Address, 855))
        {
            IsLifetimeAuthoritativeValue = false,
        };
        using var service = NewService(native, manager);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.Null(service.CloneActor(actor));
        Assert.Equal(0, native.CreateCalls);
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
    public void Pending_create_promotes_to_exact_delete_on_a_framework_tick()
    {
        var framework = new FakeFramework();
        var native = new FakeNative(new(801, (nint)0x801, 81))
        {
            ResolveReturnsNull = true,
        };
        using var service = NewService(native, framework: framework);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.Equal(
            SpawnOwnershipState.PendingCreate,
            Assert.Single(service.OwnershipSnapshot).State);

        // Identity becomes observable again: the unchanged per-index
        // destruction stamp proves the occupant is the object we created, so
        // the record promotes to exact PendingDelete and deletes.
        native.ResolveReturnsNull = false;
        framework.RaiseUpdate();

        Assert.Empty(service.OwnershipSnapshot);
        Assert.Single(native.Deleted);

        // The recovery tick unsubscribed at the terminal outcome.
        framework.RaiseUpdate();
        Assert.Single(native.Deleted);
    }

    [Fact]
    public void Pending_create_retires_without_native_touch_when_its_object_was_destroyed()
    {
        var framework = new FakeFramework();
        var native = new FakeNative(new(801, (nint)0x801, 81))
        {
            ResolveReturnsNull = true,
        };
        using var service = NewService(native, framework: framework);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));

        // The finalize hook observed a destruction at the created index: our
        // object is gone; whatever occupies the slot now is foreign.
        native.Stamps.NoteDestroyed((nint)0x801, 801);
        native.ResolveReturnsNull = false;
        framework.RaiseUpdate();

        Assert.Empty(service.OwnershipSnapshot);
        Assert.Empty(native.Deleted);
    }

    [Fact]
    public void Pending_create_becomes_a_non_recoverable_readout_after_the_bounded_retry_window()
    {
        long now = 0;
        var framework = new FakeFramework();
        var native = new FakeNative(new(801, (nint)0x801, 81))
        {
            ResolveReturnsNull = true,
        };
        using var service = NewService(
            native,
            framework: framework,
            clock: () => now);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        framework.RaiseUpdate();
        Assert.Equal(
            SpawnOwnershipState.PendingCreate,
            Assert.Single(service.OwnershipSnapshot).State);

        now = 6000;
        framework.RaiseUpdate();
        var record = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.NonRecoverable, record.State);

        // The readout record never touches native state again.
        native.ResolveReturnsNull = false;
        framework.RaiseUpdate();
        service.Dispose();
        Assert.Empty(native.Deleted);
        Assert.Equal(
            SpawnOwnershipState.NonRecoverable,
            Assert.Single(service.OwnershipSnapshot).State);
    }

    [Fact]
    public void Unknown_create_exception_is_an_explicit_non_recoverable_readout()
    {
        var native = new FakeNative(new(802, (nint)0x802, 82))
        {
            ThrowOnCreate = true,
        };
        using var service = NewService(native);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: true));
        var pending = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.NonRecoverable, pending.State);
        Assert.Equal(ushort.MaxValue, pending.CreatedIndex);
        Assert.Empty(native.Deleted);

        // Bulk cleanup retains the readout and never touches native for it.
        service.Dispose();
        Assert.Single(service.OwnershipSnapshot);
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
    public void Non_owned_operations_fail_closed_when_identity_cannot_be_resolved()
    {
        var bus = new FakeEventBus();
        var actor = Actor(0x920);
        var native = new FakeNative(new(921, (nint)0x921, 921));
        using var service = NewService(native, bus: bus);

        // The actor's address does not resolve to any current native object.
        service.SetVisibility(actor, false);
        Assert.Empty(bus.Published);
        Assert.Null(native.DrawEnabled);
        Assert.False(service.IsVisible(actor));
        Assert.False(service.SetCompanion(actor, new(CompanionKind.Companion, 5)));
        Assert.Null(service.GetCompanionInfo(actor));
        Assert.False(service.HasCompanionSlot(actor));
        Assert.Equal(0, service.GetModelCharaId(actor));

        // A resolution fault is refusal, not permission.
        native.ThrowOnResolve = true;
        service.SetVisibility(actor, false);
        Assert.Empty(bus.Published);
        Assert.Null(native.DrawEnabled);
        Assert.False(service.SetCompanion(actor, new(CompanionKind.Companion, 5)));
        Assert.Equal(0, service.GetModelCharaId(actor));
    }

    [Fact]
    public void Non_owned_visibility_override_is_exact_and_never_transfers_across_reuse()
    {
        var bus = new FakeEventBus();
        var actor = Actor(0x930);
        var native = new FakeNative(new(930, actor.Address, 930))
        {
            ReadyToDraw = true,
        };
        using var service = NewService(native, bus: bus);

        service.SetVisibility(actor, visible: false);
        Assert.False(native.DrawEnabled);
        Assert.Single(bus.Published);
        // The override is read back even though the native object reports
        // ready-to-draw (legacy-compatible store).
        Assert.False(service.IsVisible(actor));

        // Identical-triple reuse: the override dies with the native lifetime.
        native.ExternallyDestroyCurrent();
        Assert.True(service.IsVisible(actor));

        // GPose exit clears the store outright.
        service.SetVisibility(actor, visible: false);
        Assert.False(service.IsVisible(actor));
        bus.Publish(new GPoseStateChangedEvent(false));
        Assert.True(service.IsVisible(actor));
    }

    [Fact]
    public void Gpose_exit_destroys_owned_actors_exactly_and_retains_failed_deletes()
    {
        var bus = new FakeEventBus();
        var actor = Actor(0x940);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(940, actor.Address, 940));
        using var service = NewService(native, manager, bus: bus);

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));

        native.DeleteResult = false;
        bus.Publish(new GPoseStateChangedEvent(false));
        var retained = Assert.Single(service.OwnershipSnapshot);
        Assert.Equal(SpawnOwnershipState.PendingDelete, retained.State);
        Assert.Empty(native.Deleted);

        native.DeleteResult = true;
        bus.Publish(new GPoseStateChangedEvent(false));
        Assert.Empty(service.OwnershipSnapshot);
        Assert.Single(native.Deleted);
    }

    [Fact]
    public void Delayed_companion_draw_runs_only_while_the_exact_descriptor_survives()
    {
        var framework = new FakeFramework();
        var actor = Actor(0x950);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(950, actor.Address, 950));
        using var service = NewService(native, manager, framework: framework);

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: true));
        Assert.True(service.SetCompanion(actor, new(CompanionKind.Companion, 5)));
        Assert.Equal(new CompanionAttachment(CompanionKind.Companion, 5), native.Companion);
        Assert.False(native.CompanionDrawEnabled);

        native.CompanionReady = true;
        framework.RaiseUpdate();
        Assert.True(native.CompanionDrawEnabled);
    }

    [Fact]
    public void Delayed_companion_draw_refuses_after_identical_triple_reuse()
    {
        var framework = new FakeFramework();
        var actor = Actor(0x951);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(951, actor.Address, 951));
        using var service = NewService(native, manager, framework: framework);

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: true));
        Assert.True(service.SetCompanion(actor, new(CompanionKind.Companion, 5)));

        native.CompanionReady = true;
        native.ExternallyDestroyCurrent();
        framework.RaiseUpdate();
        Assert.False(native.CompanionDrawEnabled);
    }

    [Fact]
    public void Delayed_callbacks_never_schedule_without_lifetime_authority()
    {
        var framework = new FakeFramework();
        var actor = Actor(0x952);
        var native = new FakeNative(new(952, actor.Address, 952))
        {
            IsLifetimeAuthoritativeValue = false,
        };
        using var service = NewService(native, framework: framework);

        // Same-call resolution-gated writes on a non-owned actor stay
        // allowed; only cross-frame authority is refused.
        Assert.True(service.SetCompanion(actor, new(CompanionKind.Companion, 5)));
        native.CompanionReady = true;
        framework.RaiseUpdate();
        Assert.False(native.CompanionDrawEnabled);
    }

    [Fact]
    public void Every_public_operation_refuses_off_the_framework_thread()
    {
        var framework = new FakeFramework { InThread = false };
        var bus = new FakeEventBus();
        var actor = Actor(0x960);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(960, actor.Address, 960));
        using var service = NewService(native, manager, framework: framework, bus: bus);

        Assert.Null(service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.Equal(0, native.CreateCalls);
        Assert.False(service.DestroyActor(actor));
        service.SetVisibility(actor, false);
        Assert.Empty(bus.Published);
        Assert.Null(native.DrawEnabled);
        Assert.False(service.IsVisible(actor));
        Assert.False(service.SetCompanion(actor, new(CompanionKind.Companion, 5)));
        Assert.Equal(0, service.GetModelCharaId(actor));
        Assert.False(service.IsSpawnedActor(actor));

        framework.InThread = true;
        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.True(service.IsSpawnedActor(actor));
    }

    [Fact]
    public void Model_chara_redraw_completes_through_the_exact_descriptor_poll()
    {
        var framework = new FakeFramework();
        var actor = Actor(0x970);
        var manager = new FakeActorManager(actor);
        var native = new FakeNative(new(970, actor.Address, 970))
        {
            ReadyToDraw = false,
        };
        using var service = NewService(native, manager, framework: framework);

        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        service.SetModelCharaId(actor, 123);
        Assert.Equal(123, native.ModelId);
        Assert.False(native.DrawEnabled);

        native.ReadyToDraw = true;
        framework.RaiseUpdate();
        Assert.True(native.DrawEnabled);
    }

    [Fact]
    public void Plain_companion_and_catalog_records_keep_their_own_metadata()
    {
        var ledger = new SpawnOwnershipLedger();
        var plain = ledger.Add(new(201, (nint)0x201, 11), null, false);
        var companion = ledger.Add(new(202, (nint)0x202, 12), null, true);
        var catalog = ledger.Add(new(203, (nint)0x203, 13), CompanionKind.Mount, false);

        Assert.False(plain.HasCompanionSlot);
        Assert.True(companion.HasCompanionSlot);
        Assert.Equal(CompanionKind.Mount, catalog.Kind);
        Assert.Equal(3, ledger.Snapshot.Count);
    }

    [Fact]
    public void Binding_requires_the_exact_actor_identity_and_native_descriptor()
    {
        var ledger = new SpawnOwnershipLedger();
        var actor = Actor(0x301);
        var record = ledger.Add(new(301, actor.Address, 31), null, false);

        // Wrong logical identity refuses even at the right address.
        Assert.False(ledger.Bind(record.Token, actor, new EntityId("someone-else")));
        Assert.Null(record.Actor);

        Assert.True(ledger.Bind(record.Token, actor, actor.Id));
        Assert.Equal(actor.Id, record.BoundId);
        Assert.True(ledger.TryGetExact(actor, record.Descriptor!.Value, out _));
        Assert.False(ledger.TryGetExact(actor, new(301, actor.Address, 32), out _));
        Assert.False(ledger.TryGetExact(Actor(0x302), record.Descriptor!.Value, out _));

        // A different wrapper instance with a different id never matches the
        // bound record.
        var impostor = new ActorBase(new EntityId("impostor"), "Test", actor.Address);
        Assert.False(ledger.TryGetExact(impostor, record.Descriptor!.Value, out _));
        Assert.False(ledger.TryGetBound(impostor, out _));
    }

    [Fact]
    public void Index_reuse_is_fail_closed_and_does_not_inherit_metadata()
    {
        var ledger = new SpawnOwnershipLedger();
        var oldActor = Actor(0x401);
        var old = ledger.Add(new(401, oldActor.Address, 41), CompanionKind.Ornament, true);
        Assert.True(ledger.Bind(old.Token, oldActor, oldActor.Id));

        var replacement = new SpawnNativeDescriptor(401, (nint)0x402, 42);

        Assert.False(ledger.TryGetExact(oldActor, replacement, out _));
        Assert.Null(ledger.GetKind(oldActor, replacement));
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
                stage == "model" ? CompanionKind.Mount : (CompanionKind?)null,
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
        var record = ledger.Add(new(560, (nint)0x560, 56), null, false);
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
    public void Identical_triple_reuse_between_observations_refuses_ledger_cleanup()
    {
        var ledger = new SpawnOwnershipLedger();
        var record = ledger.Add(new(565, (nint)0x565, 56), null, false);
        var native = new FakeNative(record.Descriptor!.Value);

        // Same triple, but the finalize hook observed the destruction.
        native.ExternallyDestroyCurrent();
        Assert.False(SpawnOwnershipCleanup.TryDelete(ledger, native, record));
        Assert.Empty(native.Deleted);
        Assert.Single(ledger.Snapshot);
    }

    [Fact]
    public void Unresolved_manager_does_not_clear_ownership_and_bulk_cleanup_is_partial()
    {
        var ledger = new SpawnOwnershipLedger();
        var first = ledger.Add(new(501, (nint)0x501, 51), null, false);
        var second = ledger.Add(new(502, (nint)0x502, 52), null, false);

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
        var record = ledger.Add(new(601, actor.Address, 61), null, false);
        Assert.True(ledger.Bind(record.Token, actor, actor.Id));

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
        record = ledger.Add(new(701, actor.Address, 71), null, false);
        Assert.True(ledger.Bind(record.Token, actor, actor.Id));
        return ledger;
    }

    private static IActor Actor(nint address) =>
        new ActorBase(new EntityId($"test-{address}"), "Test", address);

    private static ActorSpawnService NewService(
        FakeNative native,
        FakeActorManager? manager = null,
        Action<SpawnOwnershipRecord, nint, int, string?>? mutate = null,
        FakeFramework? framework = null,
        FakeEventBus? bus = null,
        Func<long>? clock = null,
        Func<nint, EntityId?>? expectedIdentity = null) =>
        new(
            new FakeGPoseService(),
            manager ?? new FakeActorManager(),
            bus ?? new FakeEventBus(),
            native,
            () => (nint)0x100,
            null,
            framework,
            mutate ?? ((_, _, _, _) => { }),
            expectedIdentity ?? (address => new EntityId($"test-{address}")),
            clock);

    private static void ThrowNativeDelete() =>
        throw new InvalidOperationException("native delete");

    private sealed class FakeNative : IActorSpawnNativeAdapter
    {
        public FakeNative(SpawnNativeDescriptor descriptor) => Current = descriptor;

        /// <summary>The PRODUCTION destruction-stamp transition logic the real
        /// adapter feeds from the Character finalize hook; the fake feeds it
        /// from the same events (external destruction, exact deletion).</summary>
        public SpawnLifetimeStamps Stamps { get; } = new();

        public bool IsAvailableValue { get; set; } = true;
        public bool IsLifetimeAuthoritativeValue { get; set; } = true;
        public bool DeleteResult { get; set; } = true;
        public bool ThrowOnDelete { get; set; }
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnResolve { get; set; }
        public bool ResolveReturnsNull { get; set; }
        public int CreateCalls { get; private set; }
        public List<SpawnNativeDescriptor> Deleted { get; } = new();
        public SpawnNativeDescriptor? Current { get; set; }

        public bool HasSlot { get; set; } = true;
        public CompanionAttachment? Companion { get; set; }
        public bool CompanionReady { get; set; }
        public bool CompanionDrawEnabled { get; private set; }
        public int ModelId { get; set; }
        public bool ReadyToDraw { get; set; } = true;
        public bool? DrawEnabled { get; private set; }

        public bool IsAvailable => IsAvailableValue;
        public bool IsLifetimeAuthoritative => IsLifetimeAuthoritativeValue;
        public string? LifetimeAuthorityDetail =>
            IsLifetimeAuthoritativeValue ? null : "test authority unavailable";

        /// <summary>External delete observed by the finalize hook; by default
        /// the slot is immediately reused by an object with the IDENTICAL
        /// index/address/EntityId triple.</summary>
        public void ExternallyDestroyCurrent(bool reuseIdenticalTriple = true)
        {
            if (Current is not { } current)
                return;
            Stamps.NoteDestroyed(current.Address, current.Index);
            if (!reuseIdenticalTriple)
                Current = null;
        }

        public uint CreateBattleCharacter(byte reserveCompanionSlot)
        {
            CreateCalls++;
            if (ThrowOnCreate)
                throw new InvalidOperationException("create");
            return Current?.Index ?? 0xFFFFFFFF;
        }

        public ulong IndexDestructionStamp(ushort index) => Stamps.IndexStampFor(index);

        public SpawnNativeDescriptor? ResolveByIndex(ushort index)
        {
            if (ThrowOnResolve)
                throw new InvalidOperationException("resolve");
            if (!IsAvailableValue || ResolveReturnsNull)
                return null;
            return Current is { } current && current.Index == index
                ? current with { LifetimeStamp = Stamps.StampFor(current.Address) }
                : null;
        }

        public SpawnNativeDescriptor? ResolveActor(nint address)
        {
            if (ThrowOnResolve)
                throw new InvalidOperationException("resolve");
            if (!IsAvailableValue || ResolveReturnsNull)
                return null;
            return Current is { } current && current.Address == address
                ? current with { LifetimeStamp = Stamps.StampFor(current.Address) }
                : null;
        }

        public bool DeleteExact(SpawnNativeDescriptor descriptor)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("native delete");
            if (!DeleteResult)
                return false;
            Deleted.Add(descriptor);
            // Mirror the real adapter: native deletion runs the Character
            // finalize, which the hook observes.
            Stamps.NoteDestroyed(descriptor.Address, descriptor.Index);
            Current = null;
            return true;
        }

        private bool Gate(SpawnNativeDescriptor descriptor)
        {
            try
            {
                return ResolveByIndex(descriptor.Index) == descriptor;
            }
            catch
            {
                return false;
            }
        }

        public bool SetDrawState(SpawnNativeDescriptor descriptor, bool visible)
        {
            if (!Gate(descriptor))
                return false;
            DrawEnabled = visible;
            return true;
        }

        public bool? IsReadyToDraw(SpawnNativeDescriptor descriptor) =>
            Gate(descriptor) ? ReadyToDraw : null;

        public bool HasCompanionSlot(SpawnNativeDescriptor descriptor) =>
            Gate(descriptor) && HasSlot;

        public bool TryReadCompanion(
            SpawnNativeDescriptor descriptor,
            out CompanionAttachment? attachment)
        {
            bool readable = Gate(descriptor);
            attachment = readable ? Companion : null;
            return readable;
        }

        public bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id)
        {
            if (!Gate(descriptor))
                return false;
            Companion = id == 0
                ? null
                : new CompanionAttachment(kind, (ushort)id);
            return true;
        }

        public bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want) =>
            Gate(descriptor) && Companion == want && CompanionReady;

        public bool EnableCompanionDraw(SpawnNativeDescriptor descriptor)
        {
            if (!Gate(descriptor))
                return false;
            CompanionDrawEnabled = true;
            return true;
        }

        public int? ReadModelCharaId(SpawnNativeDescriptor descriptor) =>
            Gate(descriptor) ? ModelId : null;

        public bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId)
        {
            if (!Gate(descriptor))
                return false;
            ModelId = modelCharaId;
            DrawEnabled = false;
            return true;
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
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        public List<object> Published { get; } = new();
        public void Dispose() { }
        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
        }
        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }
        public void Publish<T>(T evt) where T : IEvent
        {
            Published.Add(evt!);
            if (_handlers.TryGetValue(typeof(T), out var list))
            {
                foreach (var handler in list.ToArray())
                    ((Action<T>)handler)(evt);
            }
        }
    }

    private sealed class FakeFramework : IFramework
    {
        public event IFramework.OnUpdateDelegate? Update;
        public bool InThread { get; set; } = true;
        public void RaiseUpdate() => Update?.Invoke(this);

        public DateTime LastUpdate => DateTime.MinValue;
        public DateTime LastUpdateUTC => DateTime.MinValue;
        public TimeSpan UpdateDelta => TimeSpan.Zero;
        public bool IsInFrameworkUpdateThread => InThread;
        public bool IsFrameworkUnloading => false;
        public TaskFactory GetTaskFactory() => throw new NotSupportedException();
        public Task DelayTicks(long numTicks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Run(Action action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Run(Func<Task> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Dalamud.Utility.IDebouncer CreateDebouncer(TimeSpan interval, Action action) =>
            throw new NotSupportedException();
        public Task RunOnFrameworkThread(Action action) =>
            throw new NotSupportedException();
        public Task<T> RunOnFrameworkThread<T>(Func<T> func) =>
            throw new NotSupportedException();
        public Task RunOnFrameworkThread(Func<Task> func) =>
            throw new NotSupportedException();
        public Task<T> RunOnFrameworkThread<T>(Func<Task<T>> func) =>
            throw new NotSupportedException();
        public Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RunOnTick(Func<Task> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> RunOnTick<T>(Func<Task<T>> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
