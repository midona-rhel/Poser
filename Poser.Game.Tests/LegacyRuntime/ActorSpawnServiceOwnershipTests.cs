using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Integration;
using Poser.Game;
using Poser.Game.Integration;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class ActorSpawnServiceOwnershipTests
{
    private const ushort GPoseObjectTableBase = 200;

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

    [Fact]
    public void Companion_change_does_not_create_transform_ownership()
    {
        var actor = Actor(0x844);
        var shifted = new Transform(new Vector3(500), Quaternion.Identity, Vector3.One);
        var transforms = new FakeTransformOwnership(Transform.Identity);
        var native = new FakeNative(new(844, actor.Address, 844))
        {
            OnCompanionWrite = (_, _) => transforms.GameSetPosition(shifted),
        };
        using var service = NewService(native, new FakeActorManager(actor));

        Assert.True(service.SetCompanion(
            actor, new CompanionAttachment(CompanionKind.Mount, 42)));

        Assert.Null(transforms.Override);
        Assert.Equal(shifted, transforms.NativeTransform);
    }

    [Fact]
    public void Companion_change_leaves_existing_transform_ownership_unchanged()
    {
        var actor = Actor(0x845);
        var owned = new Transform(
            new Vector3(10, 20, 30),
            Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f),
            new Vector3(1.2f));
        var shifted = new Transform(new Vector3(500), Quaternion.Identity, Vector3.One);
        var transforms = new FakeTransformOwnership(owned, owned);
        var native = new FakeNative(new(845, actor.Address, 845))
        {
            Companion = new CompanionAttachment(CompanionKind.Ornament, 8),
            OnCompanionWrite = (_, _) => transforms.GameSetPosition(shifted),
        };
        using var service = NewService(native, new FakeActorManager(actor));

        Assert.True(service.SetCompanion(
            actor, new CompanionAttachment(CompanionKind.Mount, 42)));

        Assert.Equal(owned, transforms.Override);
        Assert.Equal(owned, transforms.NativeTransform);
    }

    [Theory]
    [InlineData(CompanionKind.Companion, 42)]
    [InlineData(CompanionKind.Mount, 43)]
    public void Companion_readiness_refuses_mismatched_kind_or_id(
        CompanionKind actualKind,
        int actualId)
    {
        long now = 0;
        var actor = Actor(0x841);
        var native = new FakeNative(new(841, actor.Address, 841))
        {
            CompanionReady = true,
        };
        var framework = new FakeFramework();
        using var service = NewService(
            native,
            new FakeActorManager(actor),
            framework: framework,
            clock: () => now);
        var requested = new CompanionAttachment(CompanionKind.Mount, 42);

        Assert.True(service.SetCompanion(actor, requested));
        native.Companion = new CompanionAttachment(actualKind, (ushort)actualId);

        framework.RaiseUpdate();
        framework.RaiseUpdate();
        now = 1001;
        framework.RaiseUpdate();

        Assert.Equal(2, native.CompanionReadinessChecks);
        Assert.False(native.CompanionDrawEnabled);
    }

    [Fact]
    public void Companion_detach_accepts_empty_typed_state_with_stale_generic_child()
    {
        var actor = Actor(0x843);
        var descriptor = new SpawnNativeDescriptor(843, actor.Address, 843);
        var native = new FakeNative(descriptor)
        {
            Companion = new CompanionAttachment(CompanionKind.Mount, 9),
            GenericCompanionChildPresent = true,
        };
        var framework = new FakeFramework();
        var log = new RecordingLog();
        using var service = NewService(
            native,
            new FakeActorManager(actor),
            framework: framework,
            clock: () => 5000,
            log: log.Proxy());

        service.DestroyCompanion(actor);

        Assert.Null(native.Companion);
        Assert.NotEqual(nint.Zero, native.ReadCompanionAddress(descriptor));

        framework.RaiseUpdate();

        Assert.DoesNotContain(
            log.Warnings,
            warning => warning.Contains("timed out waiting for companion detach"));
    }

    [Fact]
    public void Companion_readiness_poll_cancels_when_owner_lifetime_changes()
    {
        var actor = Actor(0x842);
        var native = new FakeNative(new(842, actor.Address, 842))
        {
            CompanionReady = true,
        };
        var framework = new FakeFramework();
        using var service = NewService(
            native, new FakeActorManager(actor), framework: framework);

        Assert.True(service.SetCompanion(
            actor, new CompanionAttachment(CompanionKind.Ornament, 44)));
        native.ExternallyDestroyCurrent();

        framework.RaiseUpdate();
        framework.RaiseUpdate();

        Assert.Equal(0, native.CompanionReadinessChecks);
        Assert.False(native.CompanionDrawEnabled);
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
        Func<nint, EntityId?>? expectedIdentity = null,
        IPluginLog? log = null,
        FakeCollections? collections = null) =>
        new(
            new FakeGPoseService(),
            manager ?? new FakeActorManager(),
            bus ?? new FakeEventBus(),
            native,
            () => (nint)0x100,
            log,
            framework,
            mutate ?? ((_, _, _, _) => { }),
            expectedIdentity ?? (address => new EntityId($"test-{address}")),
            clock,
            collections);

    private static void ThrowNativeDelete() =>
        throw new InvalidOperationException("native delete");

    /// <summary>
    /// A faithful <see cref="ISpawnCollectionPort"/>: it records the exact
    /// address pairs it was handed, answers with the port's own result type,
    /// and — like the real Penumbra boundary — can refuse or throw without
    /// being allowed to take the spawn or the delete down with it.
    /// </summary>
    private sealed class FakeCollections : ISpawnCollectionPort
    {
        public IntegrationPortResult AssignPlayerCollection(nint cloneAddress) =>
            IntegrationPortResult.Ok();


        public List<(nint Source, nint Clone)> Inherited { get; } = new();
        public List<nint> Released { get; } = new();

        public string? InheritFailure { get; set; }
        public string? ReleaseFailure { get; set; }
        public bool ThrowOnInherit { get; set; }
        public bool ThrowOnRelease { get; set; }

        /// <summary>Observed by the spawn transaction to prove the
        /// assignment lands inside the deferred-draw window.</summary>
        public Action? OnInherit { get; set; }

        public IntegrationPortResult InheritCollection(nint sourceAddress, nint cloneAddress)
        {
            if (ThrowOnInherit)
                throw new InvalidOperationException("penumbra inherit");
            OnInherit?.Invoke();
            Inherited.Add((sourceAddress, cloneAddress));
            return InheritFailure is { } detail
                ? IntegrationPortResult.Fail(detail)
                : IntegrationPortResult.Ok();
        }

        public IntegrationPortResult ReleaseCollection(nint cloneAddress)
        {
            if (ThrowOnRelease)
                throw new InvalidOperationException("penumbra release");
            Released.Add(cloneAddress);
            return ReleaseFailure is { } detail
                ? IntegrationPortResult.Fail(detail)
                : IntegrationPortResult.Ok();
        }
    }

    /// <summary>Records Error/Warning message strings off an IPluginLog proxy,
    /// so "said once, not once per frame" is an assertion.</summary>
    private sealed class RecordingLog
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public IPluginLog Proxy()
        {
            var proxy = DispatchProxy.Create<IPluginLog, LogProxy>();
            ((LogProxy)(object)proxy).Owner = this;
            return proxy;
        }

        private class LogProxy : DispatchProxy
        {
            public RecordingLog Owner = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (args?.FirstOrDefault(a => a is string) is string message)
                {
                    if (targetMethod?.Name == "Error")
                        Owner.Errors.Add(message);
                    else if (targetMethod?.Name == "Warning")
                        Owner.Warnings.Add(message);
                }
                return null;
            }
        }
    }

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

        public bool IsAvailable { get; set; } = true;
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnResolve { get; set; }
        public bool ResolveReturnsNull { get; set; }

        /// <summary>Every slot the service asked about, in order — a readout
        /// with no created slot must never probe one (65535 is off the end of
        /// the 249-slot array).</summary>
        public List<ushort> ResolvedIndexes { get; } = new();

        /// <summary>The actor created most recently.</summary>
        public SpawnNativeDescriptor? Created { get; private set; }

        /// <summary>The Character finalize the real deletion runs, which the
        /// real adapter's hook observes.</summary>
        public Action<nint, ushort?>? OnDestroyed { get; set; }

        public uint CreateBattleCharacter(uint index, byte param)
        {
            if (ThrowOnCreate)
                throw new InvalidOperationException("create");
            if (!IsAvailable)
                return SpawnClientObjects.NoIndex;
            // The game builds in the slot it is named; uint.MaxValue means
            // "next available", which here is the seeded occupant's slot.
            var slot = index == uint.MaxValue ? Current?.Index : (ushort)index;
            if (slot is not { } created || Current is not { } occupant
                || occupant.Index != created)
                return SpawnClientObjects.NoIndex;
            Created = occupant;
            return created;
        }

        public ClientObjectSnapshot? GetObjectByIndex(ushort index)
        {
            ResolvedIndexes.Add(index);
            if (ThrowOnResolve)
                throw new InvalidOperationException("resolve");
            if (!IsAvailable || ResolveReturnsNull)
                return null;
            if (Current is not { } current || current.Index != index)
                return null;
            return new ClientObjectSnapshot(
                current.Address,
                current.EntityId,
                (ushort)(current.Index + GPoseObjectTableBase));
        }

        public uint GetIndexByObject(nint address)
        {
            if (ThrowOnResolve)
                throw new InvalidOperationException("resolve");
            if (!IsAvailable)
                return SpawnClientObjects.NoIndex;
            return Current is { } current && current.Address == address
                ? current.Index
                : SpawnClientObjects.NoIndex;
        }

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
        public List<ushort> ResolvedIndexes => Com.ResolvedIndexes;

        public bool IsAvailableValue
        {
            get => Com.IsAvailable;
            set => Com.IsAvailable = value;
        }
        public bool IsLifetimeAuthoritativeValue { get; set; } = true;
        public bool DeleteResult { get; set; } = true;
        public bool ThrowOnDelete { get; set; }
        public bool ThrowOnIndexStamp { get; set; }
        public bool IndexStampFault { get; set; }
        public bool ThrowOnCreate
        {
            get => Com.ThrowOnCreate;
            set => Com.ThrowOnCreate = value;
        }
        public bool ThrowOnResolve
        {
            get => Com.ThrowOnResolve;
            set => Com.ThrowOnResolve = value;
        }
        public bool ResolveReturnsNull
        {
            get => Com.ResolveReturnsNull;
            set => Com.ResolveReturnsNull = value;
        }

        public int CreateCalls { get; private set; }
        public List<SpawnNativeDescriptor> Deleted { get; } = new();
        public SpawnNativeDescriptor? Current
        {
            get => Com.Current;
            set => Com.Current = value;
        }

        public bool HasSlot { get; set; } = true;
        public CompanionAttachment? Companion { get; set; }
        public bool CompanionReady { get; set; }
        public int CompanionReadinessChecks { get; private set; }
        public bool CompanionDrawEnabled { get; private set; }
        public bool GenericCompanionChildPresent { get; set; }
        public Action<CompanionKind, short>? OnCompanionWrite { get; set; }
        public int ModelId { get; set; }
        public bool ReadyToDraw { get; set; } = true;
        public bool? DrawEnabled { get; private set; }

        public bool IsAvailable => Com.IsAvailable;
        public bool IsLifetimeAuthoritative => IsLifetimeAuthoritativeValue;
        public string? LifetimeAuthorityDetail =>
            IsLifetimeAuthoritativeValue ? null : "test authority unavailable";

        /// <summary>External delete observed by the finalize hook; by default
        /// the slot is immediately reused by an object with the IDENTICAL
        /// slot/address/EntityId triple.</summary>
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
            return Objects.CreateBattleCharacter(reserveCompanionSlot);
        }

        public ulong IndexDestructionStamp(ushort index)
        {
            if (ThrowOnIndexStamp)
                throw new InvalidOperationException(
                    IndexStampFault ? "stamp fault B" : "stamp fault A");
            return Objects.IndexDestructionStamp(index);
        }

        public SpawnNativeDescriptor? ResolveByIndex(ushort index) =>
            Objects.ResolveByIndex(index);

        public SpawnNativeDescriptor? ResolveActor(nint address) =>
            Objects.ResolveActor(address);

        public bool DeleteExact(SpawnNativeDescriptor descriptor)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("native delete");
            // DeleteResult false is the native call not taking effect - a
            // failure the production path cannot observe for itself.
            if (!DeleteResult || !Objects.DeleteExact(descriptor))
                return false;
            Deleted.Add(descriptor);
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

        /// <summary>What the alpha was last written to, and how many times.
        /// A hide that reached the DRAW state instead would leave this
        /// untouched and <see cref="DrawEnabled"/> false.</summary>
        public float Alpha { get; private set; } = 1f;

        public int AlphaWrites { get; private set; }

        public bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha)
        {
            if (!Gate(descriptor))
                return false;
            Alpha = alpha;
            AlphaWrites++;
            return true;
        }

        public bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target) => true;
        public bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target) => true;

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
            OnCompanionWrite?.Invoke(kind, id);
            return true;
        }

        public bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want)
        {
            CompanionReadinessChecks++;
            return Gate(descriptor) && Companion == want && CompanionReady;
        }

        /// <summary>The generic child storage address, which can outlive the
        /// typed attachment state just like the production ChildObject.</summary>
        public nint ReadCompanionAddress(SpawnNativeDescriptor descriptor) =>
            Gate(descriptor)
                && (GenericCompanionChildPresent || Companion is not null)
                    ? 0x5000
                    : nint.Zero;

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

    /// <summary>Models the independent transform-ownership boundary around a
    /// native companion write: game compensation is accepted without an
    /// override and rejected when an existing override owns the transform.</summary>
    private sealed class FakeTransformOwnership(
        Transform initial,
        Transform? existingOverride = null)
    {
        public Transform NativeTransform { get; set; } = initial;
        public Transform? Override { get; } = existingOverride;

        public void GameSetPosition(Transform requested) =>
            NativeTransform = Override ?? requested;
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
