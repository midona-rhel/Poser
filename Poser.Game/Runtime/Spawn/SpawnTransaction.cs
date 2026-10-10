using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;
using Poser.Domain.Actors;
using Poser.Domain.Companions;
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Game.Integration;
using Poser.Services;
using Poser.Domain.Identity;
using Poser.Application.Lifecycle;

namespace Poser.Game;

/// <summary>
/// One spawn, start to finish: create, record ownership, seed the body from
/// its source, defer the collection/appearance work and the first draw, and
/// bind the refreshed wrapper — or roll back exactly what was created.
/// Callers have already proven the framework thread and spawn authority.
/// </summary>
internal sealed unsafe class SpawnTransaction
{
    private const int CreateRecoveryTimeoutMs = 5000;

    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnOwnershipLedger _ownership;
    private readonly SpawnOwnershipCleanup _cleanup;
    private readonly SpawnFramePoller _poller;
    private readonly IFramework? _framework;
    private readonly ISpawnCollectionPort? _collections;
    private readonly ISpawnAppearancePort? _spawnAppearance;
    private readonly Action<SpawnOwnershipRecord, nint, int, string?> _applySpawnMutations;
    private readonly Func<nint, EntityId?> _expectedWrapperIdentity;
    private readonly Func<long> _clock;
    private readonly IPluginLog? _log;

    public SpawnTransaction(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IActorSpawnNativeAdapter native,
        SpawnOwnershipLedger ownership,
        SpawnOwnershipCleanup cleanup,
        SpawnFramePoller poller,
        IFramework? framework,
        ISpawnCollectionPort? collections,
        ISpawnAppearancePort? spawnAppearance,
        Action<SpawnOwnershipRecord, nint, int, string?>? applySpawnMutations,
        Func<nint, EntityId?> expectedWrapperIdentity,
        Func<long> clock,
        IPluginLog? log)
    {
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _native = native;
        _ownership = ownership;
        _cleanup = cleanup;
        _poller = poller;
        _framework = framework;
        _collections = collections;
        _spawnAppearance = spawnAppearance;
        _applySpawnMutations = applySpawnMutations ?? ApplySpawnMutations;
        _expectedWrapperIdentity = expectedWrapperIdentity;
        _clock = clock;
        _log = log;
    }

    /// <summary>Shared spawn path: new battle character + appearance/position copy.
    /// A null result always carries the cause in <paramref name="refusal"/>.</summary>
    public IActor? SpawnCloneFrom(
        nint sourceAddress,
        bool reserveCompanionSlot,
        out string? refusal,
        bool inheritSource = true,
        int modelCharaId = 0,
        string? name = null,
        CompanionKind? kind = null)
    {
        SpawnOwnershipRecord? ownership = null;
        refusal = null;
        try
        {
            uint idCheck = _native.CreateBattleCharacter(
                (byte)(reserveCompanionSlot ? 1 : 0));
            if (idCheck == 0xFFFFFFFF)
            {
                _log?.Warning("ActorSpawnService: Failed to create character - invalid ID");
                refusal = "The game has no free GPose actor slot (the actor table is full).";
                return null;
            }

            // The per-index destruction stamp taken here is the create-time
            // baseline: while it is unchanged, no object at this index has
            // been destroyed, so the occupant is the object we just created.
            ownership = _ownership.AddPending(
                (ushort)idCheck,
                kind,
                reserveCompanionSlot,
                _native.IndexDestructionStamp((ushort)idCheck));

            // Capture ownership before the first native mutation after create.
            // The descriptor is the only safe authority when an object-table
            // index is reused between frames.
            var descriptor = _native.ResolveByIndex((ushort)idCheck);
            if (descriptor is null)
            {
                _log?.Warning("ActorSpawnService: Created character could not be resolved");
                ScheduleCreateRecovery(ownership);
                refusal = "The created actor could not be resolved in the object table.";
                return null;
            }

            ownership.Resolve(descriptor.Value);
            _applySpawnMutations(
                ownership,
                sourceAddress,
                modelCharaId,
                name ?? SpawnNames.ForSlot(descriptor.Value.Index));
            // Penumbra cannot place the copy on the frame it is created
            // (CollectionMissing, 00:14) and can one tick later (00:2x): the
            // inherit runs next tick, still ahead of the draw. The flags
            // copy is a plain field write and lands now.
            if (sourceAddress != nint.Zero
                && _native.ResolveActor(sourceAddress) is { } flagSource)
            {
                _native.CopyDrawnAppearance(flagSource, descriptor.Value);
                _native.CopyEquipmentVisibility(flagSource, descriptor.Value);
            }
            var seeded = descriptor.Value;
            var appearanceSource = inheritSource ? _native.ResolveActor(sourceAddress) : null;
            void PrepareDraw()
            {
                if (!_poller.IsCallbackCurrent(ownership.Token, seeded)) return;
                InheritSourceCollection(ownership, sourceAddress, seeded, inheritSource);
                if (_spawnAppearance is not null)
                {
                    EnsureCurrent(ownership);
                    // Both ModelData and BaseData can outlive this slot's occupant.
                    // A duplicate must replace the former from its source rather
                    // than reverting to a possibly stale BaseData snapshot.
                    var reset = inheritSource
                        ? appearanceSource is { } original && _native.ResolveActor(sourceAddress) == original
                            ? _spawnAppearance.CopySpawnAppearance(sourceAddress, seeded.Address)
                            : IntegrationResult.Fail("The duplicate's appearance source is no longer available.")
                        : _spawnAppearance.ResetSpawnAppearance(seeded.Address);
                    if (!reset.Success)
                        _log?.Warning($"ActorSpawnService: spawn appearance could not be initialized: {reset.Detail}");
                }
            }
            if (_framework is null)
                PrepareDraw();
            else
                _framework.RunOnTick(PrepareDraw, delayTicks: 1);
            DrawWhenReady(ownership, descriptor.Value);

            _log?.Debug($"ActorSpawnService: Spawned clone at index {descriptor.Value.Index}");

            // Refresh actor list and find the new actor
            _actorManager.RefreshActors();

            // Refresh can replace the wrapper while the native slot remains
            // occupied. Re-resolve the slot before binding any wrapper.
            var afterRefresh = _native.ResolveByIndex(ownership.Descriptor!.Value.Index);
            if (afterRefresh is null
                || afterRefresh.Value != ownership.Descriptor.Value)
                throw new InvalidOperationException("Spawned actor identity changed after refresh");

            // Bind requires the wrapper's logical identity, not just its
            // address: a wrapper minted for a different logical entity at the
            // same address must refuse.
            var expectedId = _expectedWrapperIdentity(descriptor.Value.Address);
            if (expectedId is null)
                throw new InvalidOperationException("Spawned wrapper identity could not be derived");

            foreach (var actor in _actorManager.Actors)
            {
                if (actor.Address == descriptor.Value.Address)
                {
                    if (actor.Id != expectedId.Value)
                        throw new InvalidOperationException("Spawned wrapper identity mismatch after refresh");
                    if (!_ownership.Bind(ownership.Token, actor, expectedId.Value))
                        throw new InvalidOperationException("Spawned actor binding changed");
                    return actor;
                }
            }

            throw new InvalidOperationException("Spawned actor was not present after refresh");
        }
        catch (Exception ex)
        {
            refusal = $"The spawn failed: {ex.Message}";
            if (ownership is null)
            {
                // Create itself faulted: no index is known, so there is
                // nothing native we could ever prove ownership of again.
                _ownership.AddNonRecoverable(kind, reserveCompanionSlot);
                _log?.Error(
                    $"ActorSpawnService: create faulted without an index; retained as non-recoverable readout: {ex.Message}");
            }
            else if (ownership.State == SpawnOwnershipState.PendingCreate)
            {
                ScheduleCreateRecovery(ownership);
                _log?.Error($"ActorSpawnService: Failed to spawn clone: {ex.Message}");
            }
            else
            {
                var deleted = _cleanup.TryDelete(ownership);
                _log?.Error($"ActorSpawnService: Failed to spawn clone: {ex.Message}");
                if (deleted)
                {
                    // The transaction refreshed the actor list mid-flight, so a
                    // wrapper for the object we just deleted is still published.
                    // DestroyActor refreshes after its delete for the same
                    // reason; the rollback arm owes the list the same repair
                    // rather than leaving a dead wrapper until the next scan.
                    // Guarded because this runs from a catch arm: the refresh
                    // publishes, and a faulting subscriber must not replace the
                    // spawn failure with a second, unrelated exception.
                    try
                    {
                        _actorManager.RefreshActors();
                    }
                    catch (Exception refreshEx)
                    {
                        _log?.Error(
                            $"ActorSpawnService: rollback refresh failed; a deleted wrapper may persist until the next scan: {refreshEx.Message}");
                    }
                }
            }
            return null;
        }
    }

    private void ScheduleCreateRecovery(SpawnOwnershipRecord ownership)
    {
        if (ownership.CreatedIndex == ushort.MaxValue)
            return;
        if (_framework is null)
            return; // GPose-exit/dispose cleanup still promotes synchronously.

        var deadline = _clock() + CreateRecoveryTimeoutMs;
        // The tick runs every frame until a terminal outcome, so a fault that
        // reproduces every frame is said once — but keyed by type and message,
        // because a NEW fault inside the window is news, not the same line.
        string? lastFault = null;
        void Tick(IFramework fw)
        {
            try
            {
                if (_poller.IsStopped || ownership.State != SpawnOwnershipState.PendingCreate)
                {
                    _framework.Update -= Tick;
                    return;
                }
                if (_cleanup.TryFinishPendingCreate(ownership))
                {
                    _framework.Update -= Tick;
                    return;
                }
            }
            catch (Exception ex)
            {
                var fault = $"{ex.GetType().FullName}: {ex.Message}";
                if (fault != lastFault)
                {
                    lastFault = fault;
                    _log?.Warning(
                        $"ActorSpawnService: pending-create recovery for index {ownership.CreatedIndex} faulted: {fault}");
                }
            }
            if (_clock() > deadline)
            {
                ownership.MarkNonRecoverable();
                _log?.Error(
                    $"ActorSpawnService: created index {ownership.CreatedIndex} could not be recovered; retained as non-recoverable readout");
                _framework.Update -= Tick;
            }
        }
        _framework.Update += Tick;
    }

    private void ApplySpawnMutations(
        SpawnOwnershipRecord ownership,
        nint sourceAddress,
        int modelCharaId,
        string? name)
    {
        if (ownership.Descriptor is not { } descriptor)
            throw new InvalidOperationException("Spawned object has no resolved identity");
        var newObject = (GameObject*)descriptor.Address;
        EnsureCurrent(ownership);
        var newCharacter = (Character*)newObject;

        // Set a name for the character (like Brio does).
        EnsureCurrent(ownership);
        SetName(newObject, name ?? SpawnNames.ForSlot(descriptor.Index));

        // Brio registers the new body with GPose BEFORE the appearance copy
        // (ActorSpawnService.cs:325-327): the second copy exists to trigger a
        // redraw for Penumbra/Glamourer, and those tools decide what to apply
        // from what the object IS at that moment.
        EnsureCurrent(ownership);
        AddCharacterToGPose(newCharacter);

        // Copy appearance from the source actor.
        var sourceCharacter = (Character*)sourceAddress;
        EnsureCurrent(ownership);
        newCharacter->CharacterSetup.CopyFromCharacter(
            sourceCharacter,
            CharacterSetupContainer.CopyFlags.WeaponHiding | CharacterSetupContainer.CopyFlags.Position);

        // Copy again to trigger redraws for tools like Penumbra.
        EnsureCurrent(ownership);
        newCharacter->CharacterSetup.CopyFromCharacter(
            newCharacter,
            CharacterSetupContainer.CopyFlags.None);

        // Catalog spawns write the model before the first draw.
        if (modelCharaId != 0)
        {
            EnsureCurrent(ownership);
            newCharacter->ModelContainer.ModelCharaId = modelCharaId;
        }

        EnsureCurrent(ownership);
        newObject->Position = sourceCharacter->GameObject.Position;
        newObject->Rotation = sourceCharacter->GameObject.Rotation;
        newObject->DefaultPosition = sourceCharacter->GameObject.Position;
        newObject->DefaultRotation = sourceCharacter->GameObject.Rotation;

        // The draw is NOT started here: see DrawWhenReady, which the spawn
        // transaction runs once the mutations are done.
    }

    /// <summary>
    /// The clone wears the source's MODS, not just its raw appearance.
    /// Penumbra resolves a GPose actor through the parent index its
    /// CopyCharacter hook recorded (Penumbra CutsceneService.cs:123-130), and
    /// the second, self-directed CharacterSetup copy above points that parent
    /// at the clone itself — so the clone resolves under its own name and
    /// inherits nothing. Brio's clone path never repairs that (its
    /// ActorSpawnService.cs:108-172 makes no Penumbra call at all) and leaves
    /// the user to pick a collection by hand afterwards
    /// (Brio ActorAppearanceCapability.cs:210-235); Poser copies the source's
    /// effective collection instead.
    ///
    /// It runs INSIDE the deferred-draw window, before the draw object is
    /// ever built, which is the same window the copy itself needs. A refusal
    /// is reported and never fails the spawn: an unmodded clone is still a
    /// clone. The self-copy is also what makes this safe — without it the
    /// assignment would land on the SOURCE's identifier and rewrite the
    /// user's own character collection.
    /// </summary>
    private void InheritSourceCollection(
        SpawnOwnershipRecord ownership,
        nint sourceAddress,
        SpawnNativeDescriptor descriptor,
        bool inheritSource)
    {
        if (_collections is null || sourceAddress == nint.Zero)
            return;
        EnsureCurrent(ownership);
        IntegrationResult result;
        try
        {
            // A duplicate carries a snapshot of its source's mods (a locked
            // or synced source cannot be assigned by name); a plain spawn
            // simply wears the player's collection.
            result = inheritSource
                ? _collections.InheritCollection(sourceAddress, descriptor.Address)
                : _collections.AssignPlayerCollection(descriptor.Address);
        }
        catch (Exception ex)
        {
            _log?.Warning(
                $"ActorSpawnService: the clone could not inherit the source's Penumbra collection: {ex.Message}");
            return;
        }
        if (result.Success)
            ownership.MarkCollectionAssigned();
        else
            _log?.Warning(
                $"ActorSpawnService: the clone could not inherit the source's Penumbra collection: {result.Detail}");
    }

    /// <summary>
    /// Brio's <c>ActorRedrawService.DrawWhenReady</c> (ActorSpawnService.cs:156
    /// → ActorRedrawService.cs:99-110): skip two frames, then hold the draw
    /// until <c>IsReadyToDraw</c>, and only then enable it. Drawing in the same
    /// tick as the appearance copy builds the draw object from whatever was
    /// still resident and renders the BASE appearance instead of the source's —
    /// the skipped frames are also the window Penumbra/Glamourer need to react
    /// to the copy's redraw before the object is built. Without a framework
    /// there is no way to defer, so the draw is started immediately.
    /// </summary>
    private void DrawWhenReady(
        SpawnOwnershipRecord ownership,
        SpawnNativeDescriptor descriptor)
    {
        if (_framework is null)
        {
            _native.EnableDraw(descriptor);
            return;
        }
        // Unbounded while the spawn is live: a clone that is never drawn is
        // invisible and unposable for good, so a busy frame must cost time,
        // never the body. The poll ends when the record or the slot goes.
        _poller.PollUntil(
            ownership,
            descriptor,
            () => _native.IsReadyToDraw(descriptor) == true,
            () => _native.EnableDraw(descriptor),
            timeoutMs: null,
            what: $"clone draw at index {descriptor.Index}",
            skipFrames: 2);
    }

    private void EnsureCurrent(SpawnOwnershipRecord ownership)
    {
        if (ownership.Descriptor is not { } expected)
            throw new InvalidOperationException("Spawned object has no resolved identity");
        var current = _native.ResolveByIndex(expected.Index);
        if (current is null || current.Value != expected)
            throw new InvalidOperationException("Spawned object identity changed");
    }

    public void AddCharacterToGPose(Character* character)
    {
        if (!_gPoseService.IsGPosing)
            return;

        var ef = EventFramework.Instance();
        if (ef == null)
            return;

        ef->EventSceneModule.EventGPoseController.AddCharacterToGPose(character);
    }

    private static void SetName(GameObject* gameObject, string name)
    {
        for (int x = 0; x < name.Length && x < 64; x++)
        {
            gameObject->Name[x] = (byte)name[x];
        }
        gameObject->Name[Math.Min(name.Length, 63)] = 0;
    }
}
