using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Game.Bindings;
using Poser.Game.Core;
using Poser.Documents.Appearance;
using Poser.Game.Services;
using static Poser.Game.Integration.IntegrationIpc;

namespace Poser.Game.Integration;

/// <summary>
/// Penumbra's raw, version-gated call gates (no API package — raw Dalamud
/// subscribers with enums as ints, matching the current provider). Endpoint
/// labels are pinned against Penumbra.Api 874a377 (breaking 5, temporary
/// collections at V6). Every actor-targeted call resolves the exact stable
/// generation to an object index at the call boundary through
/// <see cref="IntegrationActorResolution"/>; nothing native is retained.
/// </summary>
public sealed class PenumbraIpc : IPenumbraPort, ISpawnCollectionPort, IDisposable
{
    private const int PenumbraEcSuccess = 0;
    private const int PenumbraEcNothingChanged = 1;
    private const int PenumbraEcCollectionMissing = 2;

    private readonly IntegrationActorResolution _actors;
    private readonly ActorRedrawBarrier _redraw;
    private readonly DuplicateCollectionLedger _ledger = new();
    private readonly IpcAvailability _availability;

    private readonly ICallGateSubscriber<(int Breaking, int Features)> _penumbraVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getCollections;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _getCollectionForObject;
    private readonly ICallGateSubscriber<int, Guid?, bool, bool, (int, (Guid, string)?)> _setCollectionForObject;
    private readonly ICallGateSubscriber<string, string, (int, Guid)> _createTemporaryCollection;
    private readonly ICallGateSubscriber<Guid, int> _deleteTemporaryCollection;
    private readonly ICallGateSubscriber<Guid, int, bool, int> _assignTemporaryCollection;
    private readonly ICallGateSubscriber<string, Guid, Dictionary<string, string>, string, int, int> _addTemporaryMod;
    private readonly ICallGateSubscriber<int, string> _getMetaManipulations;
    private readonly ICallGateSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]> _getResourcePaths;
    private readonly ICallGateSubscriber<string, int, string> _resolveGameObjectPath;
    private readonly ICallGateSubscriber<string> _getModDirectory;
    private readonly ICallGateSubscriber<int, int, object?> _redrawObject;
    private readonly ICallGateSubscriber<string, Guid, int, int> _removeTemporaryMod;

    public PenumbraIpc(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        Lazy<StableBindingRegistry> bindings,
        ISkeletonService skeletons,
        Poser.Application.Lifecycle.ISessionGenerationSource sessions,
        IntegrationActorResolution actors)
    {
        _actors = actors;
        _redraw = new ActorRedrawBarrier(new PenumbraRedrawRuntime(
            pluginInterface, framework, bindings, skeletons, sessions,
            () => Penumbra.Available, RequestRedraw));

        _penumbraVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
        _getCollections = pluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5");
        _getCollectionForObject = pluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _setCollectionForObject = pluginInterface.GetIpcSubscriber<int, Guid?, bool, bool, (int, (Guid, string)?)>("Penumbra.SetCollectionForObject.V5");
        _createTemporaryCollection = pluginInterface.GetIpcSubscriber<string, string, (int, Guid)>("Penumbra.CreateTemporaryCollection.V6");
        _deleteTemporaryCollection = pluginInterface.GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5");
        _assignTemporaryCollection = pluginInterface.GetIpcSubscriber<Guid, int, bool, int>("Penumbra.AssignTemporaryCollection.V5");
        _addTemporaryMod = pluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5");
        _getMetaManipulations = pluginInterface.GetIpcSubscriber<int, string>("Penumbra.GetMetaManipulations.V5");
        _getResourcePaths = pluginInterface.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5");
        _resolveGameObjectPath = pluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath");
        _getModDirectory = pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        _redrawObject = pluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");
        _removeTemporaryMod = pluginInterface.GetIpcSubscriber<string, Guid, int, int>("Penumbra.RemoveTemporaryMod.V5");

        _availability = new IpcAvailability(pluginInterface, "Penumbra", "Penumbra", () =>
        {
            var (breaking, _) = _penumbraVersion.InvokeFunc();
            return breaking == 5
                ? null
                : $"Penumbra's API v{breaking} is not supported (needs v5).";
        });
    }

    public IntegrationAvailability Penumbra => _availability.Current;

    // ── Penumbra ─────────────────────────────────────────────────────────

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetCollections() =>
        Guarded(Penumbra, "Collections", () =>
        {
            var collections = _getCollections.InvokeFunc();
            IReadOnlyList<ExternalItem> items = collections
                .Select(pair => new ExternalItem(pair.Key, pair.Value))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(items);
        });

    public IntegrationValue<Guid> GetPlayerCollection() =>
        Guarded(Penumbra, "Player collection", () =>
        {
            var (valid, _, (id, _)) = _getCollectionForObject.InvokeFunc(0);
            return valid
                ? IntegrationValue<Guid>.Ok(id)
                : IntegrationValue<Guid>.Fail("Penumbra cannot identify the player.");
        });

    public IntegrationValue<CollectionAssignment> GetCollectionAssignment(ActorId actor) =>
        Guarded(Penumbra, "Collection", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<CollectionAssignment>.Fail(detail!);
            var (valid, individual, (id, name)) = _getCollectionForObject.InvokeFunc(index);
            return valid
                ? IntegrationValue<CollectionAssignment>.Ok(new CollectionAssignment(id, name, individual))
                : IntegrationValue<CollectionAssignment>.Fail("Penumbra cannot identify this actor.");
        });

    public IntegrationResult SetIndividualCollection(ActorId actor, Guid collection) =>
        Guarded(Penumbra, "Set collection", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            return ChangeIndividualCollection(actor, index, new(true, collection));
        });

    public IntegrationResult RestoreCollection(ActorId actor, CollectionBaseline baseline) =>
        Guarded(Penumbra, "Restore collection", () =>
        {
            if (baseline.InheritedCollection is { } inherited)
                return RestoreInheritedCollection(actor, inherited);
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            return ChangeIndividualCollection(actor, index, baseline);
        });

    private IntegrationResult ChangeIndividualCollection(ActorId actor, int index, CollectionBaseline next)
    {
        var address = _actors.AddressOf(actor);
        var (valid, individual, (effective, _)) = _getCollectionForObject.InvokeFunc(index);
        if (!valid) return IntegrationResult.Fail("Penumbra cannot identify this actor.");
        bool hasOwned = _ledger.TryGetOwned(address, out var owned);
        if (!individual && effective != Guid.Empty && (!hasOwned || effective != owned)
            && !_getCollections.InvokeFunc().ContainsKey(effective))
            return IntegrationResult.Fail("Another plugin now owns the actor's temporary collection.");
        var (ec, _) = next.HadIndividualAssignment
            ? _setCollectionForObject.InvokeFunc(index, next.IndividualCollection, true, false)
            : _setCollectionForObject.InvokeFunc(index, null, false, true);
        var result = PenumbraResult(ec, "assigning the collection");
        if (!result.Success || !hasOwned) return result;

        // An individual assignment does not displace a temporary collection's
        // render resolution. Delete only our duplicate collection, after the
        // ordinary assignment succeeds; the session/history retains its data.
        int deleted = _deleteTemporaryCollection.InvokeFunc(owned);
        if (deleted is PenumbraEcSuccess or PenumbraEcNothingChanged or PenumbraEcCollectionMissing)
        {
            _ledger.Forget(address);
            return IntegrationResult.Ok();
        }
        var (rollback, _) = individual
            ? _setCollectionForObject.InvokeFunc(index, effective, true, false)
            : _setCollectionForObject.InvokeFunc(index, null, false, true);
        return IntegrationResult.Fail($"The duplicate collection could not be released (code {deleted}); "
            + (PenumbraResult(rollback, "restoring the assignment").Success
                ? "the previous assignment was restored." : $"restoring the previous assignment also failed (code {rollback})."));
    }

    public IntegrationValue<Guid> CreateTemporaryCollection(string name) =>
        Guarded(Penumbra, "Temporary collection", () =>
        {
            var (createEc, collection) = _createTemporaryCollection.InvokeFunc("Poser", name);
            return createEc == PenumbraEcSuccess
                ? IntegrationValue<Guid>.Ok(collection)
                : IntegrationValue<Guid>.Fail(
                    $"Penumbra failed creating the temporary collection (code {createEc}).");
        });

    public IntegrationResult AssignTemporaryCollection(Guid collection, ActorId actor) =>
        Guarded(Penumbra, "Assign temporary collection", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            // forceAssignment is REQUIRED for actors with an ordinary
            // individual assignment (force:false answers
            // CharacterCollectionExists) and is what lets the temporary
            // overlay that assignment while preserving it underneath. It
            // would also delete an existing temporary assignment — which
            // is why the session classifies the effective assignment in
            // the same framework action immediately before this call and
            // refuses foreign temporaries there; nothing can interleave.
            int assignEc = _assignTemporaryCollection.InvokeFunc(
                collection, index, /*forceAssignment*/ true);
            if (assignEc == PenumbraEcSuccess
                && _ledger.Owns(_actors.AddressOf(actor)))
                _ledger.MarkDisplaced(collection, actor);
            return assignEc == PenumbraEcSuccess
                ? IntegrationResult.Ok()
                : IntegrationResult.Fail(
                    $"Penumbra failed assigning the temporary collection (code {assignEc}).");
        });

    public IntegrationResult AddTemporaryMods(
        Guid collection, IReadOnlyDictionary<string, string> paths, string manipulations) =>
        Guarded(Penumbra, "Temporary mods", () =>
        {
            int ec = _addTemporaryMod.InvokeFunc(
                "PoserMCDF",
                collection,
                paths.ToDictionary(pair => pair.Key, pair => pair.Value),
                manipulations,
                0);
            return PenumbraResult(ec, "adding the temporary mod");
        });

    // ── Penumbra: invisible skin (Ktisis AssignInvisibleSkin) ────────────

    /// <summary>The invisible-skin mod's tag and priority. The priority
    /// mirrors Ktisis (100): the remap must beat whatever ordinary mods the
    /// effective collection already resolves for the same materials.</summary>
    private const string InvisibleSkinTag = "Poser_InvisibleSkin";
    private const int InvisibleSkinPriority = 100;

    public IntegrationValue<Guid> GetEffectiveCollection(ActorId actor) =>
        Guarded(Penumbra, "Effective collection", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<Guid>.Fail(detail!);
            var (valid, _, (id, _)) = _getCollectionForObject.InvokeFunc(index);
            return valid
                ? IntegrationValue<Guid>.Ok(id)
                : IntegrationValue<Guid>.Fail(
                    "Penumbra reports no collection for this actor.");
        });

    public IntegrationResult AddInvisibleSkinMods(
        Guid collection, IReadOnlyDictionary<string, string> paths) =>
        Guarded(Penumbra, "Invisible skin", () =>
        {
            int ec = _addTemporaryMod.InvokeFunc(
                InvisibleSkinTag,
                collection,
                paths.ToDictionary(pair => pair.Key, pair => pair.Value),
                string.Empty,
                InvisibleSkinPriority);
            return PenumbraResult(ec, "adding the invisible-skin mod");
        });

    public IntegrationResult RemoveInvisibleSkinMods(Guid collection) =>
        Guarded(Penumbra, "Invisible skin cleanup", () =>
        {
            int ec = _removeTemporaryMod.InvokeFunc(
                InvisibleSkinTag, collection, InvisibleSkinPriority);
            return PenumbraResult(ec, "removing the invisible-skin mod");
        });

    public IntegrationResult DeleteTemporaryCollection(Guid collection) =>
        Guarded(Penumbra, "Delete temporary collection", () =>
        {
            int ec = _deleteTemporaryCollection.InvokeFunc(collection);
            // An already-absent collection (CollectionMissing) is an
            // idempotent cleanup success, like Customize+ ProfileNotFound
            // and Glamourer NothingDone.
            if (ec is not (PenumbraEcSuccess or PenumbraEcNothingChanged or PenumbraEcCollectionMissing))
                return IntegrationResult.Fail(
                    $"Penumbra failed deleting the temporary collection (code {ec}).");
            if (!_ledger.TryGetDisplaced(collection, out var actor))
                return IntegrationResult.Ok();
            int index = _actors.ResolveIndex(actor, out _);
            if (index >= 0
                && _ledger.TryGetOwned(_actors.AddressOf(actor), out var inherited))
            {
                // Removing an MCDF drops its assignment, not the duplicate's
                // still-owned collection. Reattach that collection without force:
                // a later external assignment must never be displaced by cleanup.
                var (valid, individual, (effective, _)) = _getCollectionForObject.InvokeFunc(index);
                if (!valid) return IntegrationResult.Fail("Penumbra cannot identify the actor during collection cleanup.");
                if (!individual && effective != inherited
                    && (effective == Guid.Empty || _getCollections.InvokeFunc().ContainsKey(effective)))
                {
                    int restored = _assignTemporaryCollection.InvokeFunc(inherited, index, false);
                    if (restored != PenumbraEcSuccess)
                        return IntegrationResult.Fail(
                            $"Penumbra failed restoring the duplicate's collection (code {restored}).");
                }
            }
            _ledger.ClearDisplaced(collection);
            return IntegrationResult.Ok();
        });

    public IntegrationValue<string> GetActorMetaManipulations(ActorId actor) =>
        Guarded(Penumbra, "Meta manipulations", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<string>.Fail(detail!);
            return IntegrationValue<string>.Ok(_getMetaManipulations.InvokeFunc(index));
        });

    public IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetActorResourcePaths(ActorId actor) =>
        Guarded(Penumbra, "Resource paths", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Fail(detail!);
            var trees = _getResourcePaths.InvokeFunc(new[] { (ushort)index });
            if (trees.Length == 0 || trees[0] is not { } tree)
                return IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Fail(
                    "Penumbra reported no resources for this actor.");
            IReadOnlyDictionary<string, IReadOnlyList<string>> mapped = tree.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToList());
            return IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Ok(mapped);
        });

    public IntegrationValue<string> GetModDirectory() =>
        Guarded(Penumbra, "Mod directory", () =>
            IntegrationValue<string>.Ok(_getModDirectory.InvokeFunc()));

#if DEBUG
    public IntegrationValue<string> DebugResolveResourcePath(ActorId actor, string path) =>
        Guarded(Penumbra, "Resource resolution diagnostic", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            return index < 0 ? IntegrationValue<string>.Fail(detail!)
                : IntegrationValue<string>.Ok(_resolveGameObjectPath.InvokeFunc(path, index));
        });
#endif

    public IntegrationResult RequestRedraw(ActorId actor) =>
        Guarded(Penumbra, "Redraw", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            _redrawObject.InvokeAction(index, 0);
            return IntegrationResult.Ok();
        });

    public Task<IntegrationResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation) =>
        _redraw.RedrawAndWait(actor, timeout, cancellation);

    public void Dispose() => _redraw.Dispose();

    // ── Penumbra: spawn collection inheritance ───────────────────────────

    public IntegrationValue<SpawnCollectionSnapshot?> CaptureInheritedCollection(ActorId actor) =>
        Guarded(Penumbra, "Capture inherited collection", () =>
        {
            if (_actors.ResolveIndex(actor, out var detail) < 0)
                return IntegrationValue<SpawnCollectionSnapshot?>.Fail(detail!);
            return CaptureInheritedCollection(_actors.AddressOf(actor));
        });

    public IntegrationResult RestoreInheritedCollection(ActorId actor, SpawnCollectionSnapshot snapshot) =>
        Guarded(Penumbra, "Restore inherited collection", () =>
        {
            int index = _actors.ResolveIndex(actor, out var detail);
            if (index < 0)
                return IntegrationResult.Fail(detail!);
            var address = _actors.AddressOf(actor);
            var (valid, individual, (effective, _)) = _getCollectionForObject.InvokeFunc(index);
            if (!valid) return IntegrationResult.Fail("Penumbra cannot identify this actor.");
            // History may outlive an external reassignment. Never force away
            // another plugin's temporary collection to restore our duplicate.
            if (!individual && effective != Guid.Empty
                && (!_ledger.TryGetOwned(address, out var owned) || owned != effective)
                && !_getCollections.InvokeFunc().ContainsKey(effective))
                return IntegrationResult.Fail("Another plugin now owns the actor's temporary collection.");
            return RestoreInheritedCollection(address, snapshot);
        });

    public IntegrationValue<SpawnCollectionSnapshot?> CaptureInheritedCollection(nint actor) =>
        Guarded(Penumbra, "Capture inherited collection", () =>
        {
            if (_actors.AddressPair(actor, actor) is { } refusal)
                return IntegrationValue<SpawnCollectionSnapshot?>.Fail(refusal.Detail!);
            if (!_ledger.TryGetOwned(actor, out var owned))
                return IntegrationValue<SpawnCollectionSnapshot?>.Ok(null);
            var index = GPoseObjectTable.IndexOf(actor);
            var (valid, _, (effective, _)) = _getCollectionForObject.InvokeFunc(index);
            if (!valid || effective != owned)
                return IntegrationValue<SpawnCollectionSnapshot?>.Fail(
                    "The duplicate's collection has been replaced; its owned resources cannot be captured.");
            var trees = _getResourcePaths.InvokeFunc(new[] { (ushort)index });
            if (trees.Length == 0 || trees[0] is not { } tree)
                return IntegrationValue<SpawnCollectionSnapshot?>.Fail("Penumbra reported no resources for the duplicate.");
            var paths = CaptureRedirects(tree, path => _resolveGameObjectPath.InvokeFunc(path, index));
            return IntegrationValue<SpawnCollectionSnapshot?>.Ok(new(paths,
                _getMetaManipulations.InvokeFunc(index) ?? string.Empty));
        });

    /// <summary>Gives the clone a Poser-owned temporary collection built from
    /// the source's LIVE resolution: every loaded resource that resolves off
    /// its game path, plus the source's meta manipulations. That is exactly
    /// what the source draws with — whether its own collection is a saved
    /// one, another plugin's temporary one, or nothing — and the copy's
    /// meta and race-specific models follow (the ordinary assignment left a
    /// Miqo'te copy on Midlander fallback models).</summary>
    public IntegrationResult InheritCollection(nint sourceAddress, nint cloneAddress) =>
        Guarded(Penumbra, "Inherit collection", () =>
        {
            if (_actors.AddressPair(sourceAddress, cloneAddress) is { } refusal)
                return refusal;
            int sourceIndex = GPoseObjectTable.IndexOf(sourceAddress);
            var trees = _getResourcePaths.InvokeFunc(new[] { (ushort)sourceIndex });
            if (trees.Length == 0 || trees[0] is not { } tree)
                return IntegrationResult.Fail("Penumbra reported no resources for the source.");
            var redirects = CaptureRedirects(tree, path => _resolveGameObjectPath.InvokeFunc(path, sourceIndex));
            string manipulations = _getMetaManipulations.InvokeFunc(sourceIndex) ?? string.Empty;
            return RestoreInheritedCollection(cloneAddress, new(redirects, manipulations));
        });

    internal static Dictionary<string, string> CaptureRedirects(
        IReadOnlyDictionary<string, HashSet<string>> tree, Func<string, string> resolve)
    {
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (loadedPath, gamePaths) in tree)
            foreach (var gamePath in gamePaths)
            {
                // Resource trees can lose resolved paths after collection changes.
                // An empty redirect prevents even the base skeleton from loading.
                var resolved = string.IsNullOrWhiteSpace(loadedPath) ? resolve(gamePath) : loadedPath;
                if (string.IsNullOrWhiteSpace(resolved))
                    throw new InvalidOperationException($"Penumbra could not resolve {gamePath}.");
                if (!string.Equals(gamePath, resolved, StringComparison.OrdinalIgnoreCase))
                    redirects[gamePath] = resolved;
            }
        return redirects;
    }

    public IntegrationResult RestoreInheritedCollection(nint cloneAddress, SpawnCollectionSnapshot snapshot) =>
        Guarded(Penumbra, "Restore inherited collection", () =>
        {
            if (_actors.AddressPair(cloneAddress, cloneAddress) is { } refusal) return refusal;
            int cloneIndex = GPoseObjectTable.IndexOf(cloneAddress);
            var redirects = new Dictionary<string, string>(snapshot.Paths, StringComparer.OrdinalIgnoreCase);
            var manipulations = snapshot.Manipulations;
            var (createEc, collection) = _createTemporaryCollection.InvokeFunc(
                "Poser", $"Poser duplicate {cloneIndex}");
            if (createEc != PenumbraEcSuccess)
                return IntegrationResult.Fail(
                    $"Penumbra failed creating the duplicate's collection (code {createEc}).");
            if (redirects.Count > 0 || manipulations.Length > 0)
            {
                int modEc = _addTemporaryMod.InvokeFunc(
                    "PoserDuplicate", collection, redirects, manipulations, 0);
                if (modEc != PenumbraEcSuccess)
                {
                    _deleteTemporaryCollection.InvokeFunc(collection);
                    return IntegrationResult.Fail(
                        $"Penumbra failed filling the duplicate's collection (code {modEc}).");
                }
            }
            int assignEc = _assignTemporaryCollection.InvokeFunc(collection, cloneIndex, /*forceAssignment*/ true);
            if (assignEc != PenumbraEcSuccess)
            {
                _deleteTemporaryCollection.InvokeFunc(collection);
                return IntegrationResult.Fail(
                    $"Penumbra failed assigning the duplicate's collection (code {assignEc}).");
            }
            // A fresh body's seed assignment is no longer its authored collection.
            // Penumbra's GetCollectionForObject reports an individual assignment
            // before checking the temporary collection, even when the latter renders.
            var (clearEc, _) = _setCollectionForObject.InvokeFunc(cloneIndex, null, false, true);
            if (_ledger.Release(cloneAddress, out var stale))
                _deleteTemporaryCollection.InvokeFunc(stale);
            _ledger.Record(cloneAddress, collection);
            return PenumbraResult(clearEc, "clearing the duplicate's seed collection");
        });

    public IntegrationResult AssignPlayerCollection(nint cloneAddress) =>
        Guarded(Penumbra, "Player collection", () =>
        {
            if (_actors.AddressPair(cloneAddress, cloneAddress) is { } refusal)
                return refusal;
            // A body at a reused address owns no earlier duplicate's GUID.
            if (DeleteDuplicateCollection(cloneAddress) is { Success: false } stale)
                return stale;
            var (valid, _, (id, _)) = _getCollectionForObject.InvokeFunc(0);
            if (!valid)
                return IntegrationResult.Fail("Penumbra cannot identify the player.");
            var (ec, _) = _setCollectionForObject.InvokeFunc(
                GPoseObjectTable.IndexOf(cloneAddress), id, /*allowCreateNew*/ true, /*allowDelete*/ false);
            return PenumbraResult(ec, "assigning the player's collection");
        });

    public IntegrationResult ReleaseCollection(nint cloneAddress) =>
        Guarded(Penumbra, "Release collection", () =>
        {
            if (_actors.AddressPair(cloneAddress, cloneAddress) is { } refusal)
                return refusal;
            // The duplicate's own collection goes with it; deleting it drops
            // its assignment too.
            var deleted = DeleteDuplicateCollection(cloneAddress);
            var (ec, _) = _setCollectionForObject.InvokeFunc(
                GPoseObjectTable.IndexOf(cloneAddress), null, /*allowCreateNew*/ false, /*allowDelete*/ true);
            return deleted.Success
                ? PenumbraResult(ec, "releasing the clone's collection assignment")
                : deleted;
        });

    public IntegrationResult DiscardCollection(nint cloneAddress) =>
        Guarded(Penumbra, "Discard collection", () =>
            _actors.OnFrameworkThreadNow
                ? DeleteDuplicateCollection(cloneAddress)
                : IntegrationResult.Fail(
                    "External integration calls must run on the framework thread."));

    /// <summary>Deletes the duplicate's own temporary collection by its GUID.
    /// It needs no live object, so it also serves a clone that vanished
    /// first; the ledger entry goes either way, so no later body at the
    /// address inherits it.</summary>
    private IntegrationResult DeleteDuplicateCollection(nint cloneAddress)
    {
        if (!_ledger.Release(cloneAddress, out var own))
            return IntegrationResult.Ok();
        int ec = _deleteTemporaryCollection.InvokeFunc(own);
        return ec is PenumbraEcSuccess or PenumbraEcNothingChanged or PenumbraEcCollectionMissing
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"Penumbra failed deleting the duplicate's collection (code {ec}).");
    }

    private static IntegrationResult PenumbraResult(int ec, string what) =>
        ec is PenumbraEcSuccess or PenumbraEcNothingChanged
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail($"Penumbra failed {what} (code {ec}).");
}
