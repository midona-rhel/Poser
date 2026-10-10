using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Integration;
using Poser.Game;
using Poser.Game.Integration;
using Poser.Services;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Application.Events;
using Poser.Application.Lifecycle;

namespace Poser.Game.Tests.Runtime;

public sealed class ActorSpawnServiceOwnershipTests
{
    [Fact]
    public void Fresh_spawn_clears_retained_appearance_inside_deferred_draw_window()
    {
        const int model = 4962;
        var actor = Actor(0x900);
        var native = new FakeNative(new(9, actor.Address, 900));
        var framework = new FakeFramework();
        var order = new List<string>();
        var appearance = new FakeSpawnAppearance(address =>
        {
            Assert.Equal(actor.Address, address);
            Assert.Null(native.DrawEnabled);
            order.Add("reset");
            return IntegrationResult.Ok();
        });
        using var service = NewService(native, new FakeActorManager(actor),
            framework: framework, appearance: appearance,
            mutate: (_, _, requested, _) => { Assert.Equal(model, requested); order.Add("model"); });

        Assert.Same(actor, service.SpawnNewActor(false, model));
        Assert.Equal(new[] { "model" }, order);
        framework.RaiseUpdate();
        Assert.Equal(new[] { "model", "reset" }, order);
        Assert.Null(native.DrawEnabled);
        framework.RaiseUpdate();
        framework.RaiseUpdate();
        Assert.True(native.DrawEnabled);
        Assert.Equal(1, appearance.Calls);
    }

    [Fact]
    public void Duplicate_copies_only_its_source_state_before_first_draw()
    {
        var source = Actor(0x901);
        var actor = Actor(0x900);
        var native = new FakeNative(new(9, actor.Address, 900))
        { SourceDescriptor = new(5, source.Address, 901) };
        var framework = new FakeFramework();
        var appearance = new FakeSpawnAppearance(address =>
        {
            Assert.Equal(actor.Address, address);
            Assert.NotEqual(source.Address, address);
            Assert.Null(native.DrawEnabled);
            return IntegrationResult.Ok();
        });
        using var service = NewService(native, new FakeActorManager(actor),
            framework: framework, appearance: appearance);
        Assert.Same(actor, service.CloneActor(source));
        for (var i = 0; i < 3; i++) framework.RaiseUpdate();
        Assert.Equal(0, appearance.Calls);
        Assert.Equal((source.Address, actor.Address), Assert.Single(appearance.Copies));
        Assert.True(native.DrawEnabled);
    }

    private sealed class FakeSpawnAppearance(Func<nint, IntegrationResult> reset) : ISpawnAppearancePort
    {
        public int Calls { get; private set; }
        public List<(nint Source, nint Target)> Copies { get; } = new();
        public IntegrationResult CopySpawnAppearance(nint sourceAddress, nint targetAddress)
        {
            Copies.Add((sourceAddress, targetAddress));
            return reset(targetAddress);
        }
        public IntegrationResult ResetSpawnAppearance(nint address)
        {
            Calls++;
            return reset(address);
        }
    }

    [Fact]
    public void Model_redraw_invalidates_cached_skeletons_while_draw_is_down_not_after_address_reuse()
    {
        var actor = Actor(0x850);
        var native = new FakeNative(new(850, actor.Address, 850));
        var bus = new FakeEventBus();
        var framework = new FakeFramework();
        var invalidated = new List<EntityId>();
        bus.Subscribe<ActorDrawInvalidatedEvent>(e =>
        {
            Assert.False(native.DrawEnabled);
            invalidated.Add(e.Actor);
        });
        using var service = NewService(native, new FakeActorManager(actor), bus: bus, framework: framework);

        service.SetModelCharaId(actor, 5);
        Assert.Equal(actor.Id, Assert.Single(invalidated));
        framework.RaiseUpdate();
        Assert.True(native.DrawEnabled);
        service.SetModelCharaId(actor, 5); // No teardown for an unchanged model.
        Assert.Single(invalidated);
        service.SetModelCharaId(actor, 0);
        Assert.Equal(2, invalidated.Count);
        framework.RaiseUpdate();
        Assert.True(native.DrawEnabled);
    }

    [Fact]
    public void Spawn_replacement_and_dispose_retry_keep_exact_ownership()
    {
        var liveActor = Actor(0x820);
        var liveNative = new FakeNative(new(820, liveActor.Address, 820));
        var bus = new FakeEventBus();
        using var service = NewService(liveNative, new FakeActorManager(liveActor), bus: bus);
        Assert.Same(liveActor, service.SpawnNewActor(reserveCompanionSlot: false));
        liveNative.DeleteResult = false;
        bus.Publish(new GPoseStateChangedEvent(false));
        Assert.Equal(SpawnOwnershipState.PendingDelete, Assert.Single(service.OwnershipSnapshot).State);
        liveNative.DeleteResult = true;
        bus.Publish(new GPoseStateChangedEvent(false));
        Assert.Empty(service.OwnershipSnapshot);
    }

    [Fact]
    public void A_clone_whose_slot_vanished_first_still_has_its_collection_deleted()
    {
        var actor = Actor(0x830);
        var native = new FakeNative(new(830, actor.Address, 830));
        var bus = new FakeEventBus();
        var collections = new FakeCollections();
        using var service = new ActorSpawnService(
            new FakeGPoseService(), new FakeActorManager(actor), bus, native, () => (nint)0x100,
            null, null, (_, _, _, _) => { }, address => new EntityId($"test-{address}"),
            collections: collections);
        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        Assert.Contains($"assign:{actor.Address:X}", collections.Calls);

        native.Current = null; // The slot is emptied natively before Poser deletes it.
        bus.Publish(new GPoseStateChangedEvent(false));

        Assert.Empty(service.OwnershipSnapshot);
        Assert.Contains($"discard:{actor.Address:X}", collections.Calls);
    }

    private sealed class FakeCollections : ISpawnCollectionPort
    {
        public List<string> Calls { get; } = new();
        public IntegrationResult InheritCollection(nint sourceAddress, nint cloneAddress) =>
            Record($"inherit:{cloneAddress:X}");
        public IntegrationResult ReleaseCollection(nint cloneAddress) => Record($"release:{cloneAddress:X}");
        public IntegrationResult AssignPlayerCollection(nint cloneAddress) => Record($"assign:{cloneAddress:X}");
        public IntegrationResult DiscardCollection(nint cloneAddress) => Record($"discard:{cloneAddress:X}");
        private IntegrationResult Record(string call)
        {
            Calls.Add(call);
            return IntegrationResult.Ok();
        }
    }

    [Fact]
    public void Clone_and_visibility_refuse_stale_or_reused_native_identity()
    {
        var actor = Actor(0x900);
        var native = new FakeNative(new(900, actor.Address, 900));
        using var service = NewService(native, new FakeActorManager(actor));
        Assert.Null(service.CloneActor(Actor(0x901)));
        Assert.Same(actor, service.SpawnNewActor(reserveCompanionSlot: false));
        native.ExternallyDestroyCurrent();
        Assert.False(service.DestroyActor(actor));
        Assert.False(service.IsSpawnedActor(actor));
        Assert.Single(service.OwnershipSnapshot);
        // A reused slot no longer identifies the original actor.
        Assert.False(service.IsVisible(actor));
    }

    [Fact]
    public void Companion_readiness_skips_first_update_then_enables_exact_requested_child()
    {
        var actor = Actor(0x840);
        var native = new FakeNative(new(840, actor.Address, 840))
        {
            CompanionReady = true,
        };
        var framework = new FakeFramework();
        using var service = NewService(
            native, new FakeActorManager(actor), framework: framework);
        var requested = new CompanionAttachment(CompanionKind.Mount, 42);

        Assert.True(service.SetCompanion(actor, requested));
        Assert.Equal(requested, native.Companion);

        framework.RaiseUpdate();

        Assert.Equal(0, native.CompanionReadinessChecks);
        Assert.False(native.CompanionDrawEnabled);

        framework.RaiseUpdate();

        Assert.Equal(1, native.CompanionReadinessChecks);
        Assert.True(native.CompanionDrawEnabled);
    }

    private static IActor Actor(nint address) =>
        new ActorBase(new EntityId($"test-{address}"), "Test", address);

    private static ActorSpawnService NewService(
        FakeNative native,
        FakeActorManager? manager = null,
        Action<SpawnOwnershipRecord, nint, int, string?>? mutate = null,
        FakeFramework? framework = null,
        FakeEventBus? bus = null,
        ISpawnAppearancePort? appearance = null) =>
        new(
            new FakeGPoseService(),
            manager ?? new FakeActorManager(),
            bus ?? new FakeEventBus(),
            native,
            () => (nint)0x100,
            null,
            framework,
            mutate ?? ((_, _, _, _) => { }),
            address => new EntityId($"test-{address}"),
            spawnAppearance: appearance);

    /// <summary>
    /// A ClientObjectManager whose two index spaces really differ: the occupant
    /// of slot N reports object-table index N + 200, exactly as the game does.
    /// The production identity logic runs against this, so a descriptor built
    /// from the wrong number fails every ownership test rather than every live
    /// spawn.
    /// </summary>
    private sealed class FakeClientObjectManager : IClientObjectManagerNative
    {
        public FakeClientObjectManager(SpawnNativeDescriptor occupant) =>
            Current = occupant;

        /// <summary>The single occupied slot, or null for an empty manager.
        /// Its <c>Index</c> is the slot; its object-table index is derived.</summary>
        public SpawnNativeDescriptor? Current { get; set; }

        /// <summary>The Character finalize the real deletion runs, which the
        /// real adapter's hook observes.</summary>
        public Action<nint, ushort?>? OnDestroyed { get; set; }

        public uint CreateBattleCharacter(uint index, byte param)
        {
            // The game builds in the slot it is named; uint.MaxValue means
            // "next available", which here is the seeded occupant's slot.
            var slot = index == uint.MaxValue ? Current?.Index : (ushort)index;
            if (slot is not { } created || Current is not { } occupant
                || occupant.Index != created)
                return SpawnClientObjects.NoIndex;
            return created;
        }

        public ClientObjectSnapshot? GetObjectByIndex(ushort index)
        {
            if (Current is not { } current || current.Index != index)
                return null;
            return new ClientObjectSnapshot(
                current.Address,
                current.EntityId,
                (ushort)(current.Index + 200));
        }

        public uint GetIndexByObject(nint address) =>
            Current is { } current && current.Address == address
                ? current.Index
                : SpawnClientObjects.NoIndex;

        public void DeleteObjectByIndex(ushort index, byte param)
        {
            if (Current is not { } current || current.Index != index)
                return;
            Current = null;
            OnDestroyed?.Invoke(current.Address, index);
        }
    }

    /// <summary>
    /// Fault injection plus the still-native members (draw, companion, model).
    /// Create/resolve/delete and the destruction stamps are the PRODUCTION
    /// <see cref="SpawnClientObjects"/> running on
    /// <see cref="FakeClientObjectManager"/>, so index-space and argument-order
    /// mistakes in that logic are test failures here.
    /// </summary>
    private sealed class FakeNative : IActorSpawnNativeAdapter
    {
        public FakeNative(SpawnNativeDescriptor descriptor)
        {
            Com = new FakeClientObjectManager(descriptor);
            Objects = new SpawnClientObjects(Com);
            Com.OnDestroyed = Objects.Stamps.NoteDestroyed;
        }

        public FakeClientObjectManager Com { get; }
        public SpawnClientObjects Objects { get; }
        public SpawnLifetimeStamps Stamps => Objects.Stamps;
        public bool DeleteResult { get; set; } = true;

        public SpawnNativeDescriptor? Current
        {
            get => Com.Current;
            set => Com.Current = value;
        }

        public SpawnNativeDescriptor? SourceDescriptor { get; set; }
        public CompanionAttachment? Companion { get; set; }
        public bool CompanionReady { get; set; }
        public int CompanionReadinessChecks { get; private set; }
        public bool CompanionDrawEnabled { get; private set; }
        public int ModelId { get; set; }
        public bool? DrawEnabled { get; private set; }

        public bool IsAvailable => true;
        public bool IsLifetimeAuthoritative => true;
        public string? LifetimeAuthorityDetail => null;

        /// <summary>External delete observed by the finalize hook; the slot is
        /// immediately reused by an object with the IDENTICAL
        /// slot/address/EntityId triple.</summary>
        public void ExternallyDestroyCurrent()
        {
            if (Current is { } current)
                Stamps.NoteDestroyed(current.Address, current.Index);
        }

        public uint CreateBattleCharacter(byte reserveCompanionSlot) =>
            Objects.CreateBattleCharacter(reserveCompanionSlot);

        public ulong IndexDestructionStamp(ushort index) =>
            Objects.IndexDestructionStamp(index);

        public SpawnNativeDescriptor? ResolveByIndex(ushort index) =>
            Objects.ResolveByIndex(index);

        public SpawnNativeDescriptor? ResolveActor(nint address) =>
            SourceDescriptor is { } source && source.Address == address ? source : Objects.ResolveActor(address);

        // DeleteResult false is the native call not taking effect - a
        // failure the production path cannot observe for itself.
        public bool DeleteExact(SpawnNativeDescriptor descriptor) =>
            DeleteResult && Objects.DeleteExact(descriptor);

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

        public bool EnableDraw(SpawnNativeDescriptor descriptor)
        {
            if (!Gate(descriptor))
                return false;
            DrawEnabled = true;
            return true;
        }

        public bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha) => Gate(descriptor);

        public bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target) => true;
        public bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target) => true;

        public bool? IsReadyToDraw(SpawnNativeDescriptor descriptor) =>
            Gate(descriptor) ? true : null;

        public bool HasCompanionSlot(SpawnNativeDescriptor descriptor) =>
            descriptor != SourceDescriptor && Gate(descriptor);

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

        public bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want)
        {
            CompanionReadinessChecks++;
            return Gate(descriptor) && Companion == want && CompanionReady;
        }

        public nint ReadCompanionAddress(SpawnNativeDescriptor descriptor) =>
            Gate(descriptor) && Companion is not null ? 0x5000 : nint.Zero;

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
        public bool IsAvailable(IActor actor) => Actors.Contains(actor) || AuxiliaryActors.Contains(actor);
        public FakeActorManager(IActor? actor = null) =>
            Actors = actor is null ? Array.Empty<IActor>() : [actor];
        public IReadOnlyList<IActor> Actors { get; }
        public IReadOnlyList<IActor> AuxiliaryActors { get; } = Array.Empty<IActor>();
        public bool IsLocalPlayer(IActor actor) => false;
        public void AdoptWorldActor(nint address) { }
        public bool IsAdopted(IActor actor) => false;
        public void ReleaseWorldActor(nint address) { }
        public void Dispose() { }
        public void RegisterAuxiliary(ushort objectIndex, ActorKind kind) { }
        public void UnregisterAuxiliary(ushort objectIndex) { }
        public void RefreshActors() { }
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
        private readonly List<(Action Action, int Ticks)> _queued = new();
        public void RaiseUpdate()
        {
            var pending = _queued.ToArray();
            _queued.Clear();
            foreach (var (action, ticks) in pending)
                if (ticks <= 1) action();
                else _queued.Add((action, ticks - 1));
            Update?.Invoke(this);
        }

        public DateTime LastUpdate => DateTime.MinValue;
        public DateTime LastUpdateUTC => DateTime.MinValue;
        public TimeSpan UpdateDelta => TimeSpan.Zero;
        public bool IsInFrameworkUpdateThread => true;
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
        public Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default)
        {
            _queued.Add((action, delayTicks));
            return Task.CompletedTask;
        }
        public Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RunOnTick(Func<Task> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> RunOnTick<T>(Func<Task<T>> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
