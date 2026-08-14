using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// One Game-private observation of an overworld object. <see cref="Reference"/>
/// is the adapter's own identity handle (the live Dalamud wrapper in
/// production); it never leaves <see cref="Poser.Game"/>. Identity is the full
/// (address, object-table index, GameObjectId) triple — an index alone is a
/// hint, never identity.
/// </summary>
internal readonly record struct WorldActorObservation(
    object? Reference,
    nint Address,
    ushort ObjectIndex,
    ulong GameObjectId,
    string Name,
    WorldActorKind? Kind,
    float DistanceFromPlayer,
    bool IsDrawing);

/// <summary>
/// The read-only object-table boundary for overworld discovery. Enumeration
/// and revalidation only — no member of this seam can mutate game state.
/// </summary>
internal interface IWorldActorTableAdapter
{
    /// <summary>Raw union of the overworld enumerations (character manager,
    /// client, stand objects — Ktisis ActorService.GetOverworldActors' exact
    /// union). Unfiltered: eligibility is the discovery core's job.</summary>
    IReadOnlyList<WorldActorObservation> EnumerateOverworld();

    /// <summary>Re-observes the stored candidate through its own reference
    /// AND a fresh object-table lookup at its index; null when either fails.
    /// The caller compares the returned identity against the stored one — a
    /// changed occupant returns non-null here and is refused there.</summary>
    WorldActorObservation? Revalidate(WorldActorObservation stored);
}

/// <summary>
/// Production adapter over Dalamud's object table. Overworld actors live in
/// the character-manager band [0,199] and stand band [489,628], plus client
/// objects [200,448] other plugins spawned; all reads are framework-thread
/// per the table's own contract.
/// </summary>
internal unsafe sealed class WorldActorTableAdapter : IWorldActorTableAdapter
{
    private readonly IObjectTable _objectTable;

    public WorldActorTableAdapter(IObjectTable objectTable) =>
        _objectTable = objectTable;

    public IReadOnlyList<WorldActorObservation> EnumerateOverworld()
    {
        var origin = _objectTable.LocalPlayer?.Position;
        var observations = new List<WorldActorObservation>();
        // Ktisis ActorService.cs:49-58: the same three enumerations. The
        // kind/band/drawing filters live in WorldActorDiscovery.
        foreach (var gameObject in _objectTable.CharacterManagerObjects)
            Observe(observations, gameObject, origin);
        foreach (var gameObject in _objectTable.ClientObjects)
            Observe(observations, gameObject, origin);
        foreach (var gameObject in _objectTable.StandObjects)
            Observe(observations, gameObject, origin);
        return observations;
    }

    public WorldActorObservation? Revalidate(WorldActorObservation stored)
    {
        if (stored.Reference is not IGameObject reference || !reference.IsValid())
            return null;
        // The stored wrapper reads live memory at its remembered address; the
        // fresh table lookup proves that address is still THE occupant of the
        // stored index. Either failing is refusal, never permission.
        var current = _objectTable[stored.ObjectIndex];
        if (current is null || current.Address != reference.Address)
            return null;
        return Observation(current, _objectTable.LocalPlayer?.Position);
    }

    private static void Observe(
        List<WorldActorObservation> into, IGameObject gameObject, Vector3? origin)
    {
        if (gameObject.Address == nint.Zero)
            return;
        into.Add(Observation(gameObject, origin));
    }

    private static WorldActorObservation Observation(
        IGameObject gameObject, Vector3? origin) =>
        new(
            gameObject,
            gameObject.Address,
            gameObject.ObjectIndex,
            gameObject.GameObjectId,
            gameObject.Name.TextValue,
            ToKind(gameObject.ObjectKind),
            origin is { } from
                ? Vector3.Distance(gameObject.Position, from)
                : 0f,
            IsDrawing(gameObject.Address));

    /// <summary>Overworld kinds Poser offers for cloning: exactly the set the
    /// GPose admission scan accepts (ActorManager.GetGPoseCharacters). Mount
    /// is deliberately absent until the appearance copy is proven for it.</summary>
    private static WorldActorKind? ToKind(ObjectKind objectKind) => objectKind switch
    {
        ObjectKind.Pc => WorldActorKind.Player,
        ObjectKind.BattleNpc => WorldActorKind.BattleNpc,
        ObjectKind.EventNpc => WorldActorKind.EventNpc,
        ObjectKind.Companion => WorldActorKind.Companion,
        ObjectKind.Retainer => WorldActorKind.Retainer,
        _ => null,
    };

    /// <summary>A world object is drawing when RenderFlags == 0 — Brio
    /// ObjectMonitorService.cs:90; Ktisis GameObjectEx.cs:96-101 (its
    /// IsEnabled model-bit check is implied by the full-zero check).</summary>
    private static bool IsDrawing(nint address) =>
        ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)
            ->RenderFlags == 0;
}

/// <summary>
/// Discovery and import of visible overworld actors (execution brief §6.1).
/// Discovery is a read-only enumeration completely separate from the 201–439
/// GPose admission scan: no overworld object is ever handed to a pose or
/// mutation surface. The single crossing is <see cref="CloneCandidate"/>,
/// which revalidates the source's exact identity and funnels its address into
/// the accepted spawn ownership transaction; the clone then enters the scene
/// at its own 201–439 index through the ordinary registry scan. The source is
/// never adopted, mutated, or deleted.
/// </summary>
public sealed class WorldActorDiscovery : IWorldActorReadPort
{
    private readonly IWorldActorTableAdapter _adapter;
    private readonly IGPoseService _gPose;
    private readonly IActorManager _actorManager;
    private readonly Func<nint, IActor?> _cloneSource;
    private readonly IFramework? _framework;
    private readonly IPluginLog? _log;

    /// <summary>The current listing's observations, keyed by the opaque ids
    /// handed out. Replaced wholesale by every refresh: an id absent here is
    /// stale by construction.</summary>
    private readonly Dictionary<WorldActorCandidateId, WorldActorObservation>
        _observations = new();

    private readonly List<WorldActorCandidate> _candidates = new();

    public WorldActorDiscovery(
        IObjectTable objectTable,
        IGPoseService gPoseService,
        IActorManager actorManager,
        ActorSpawnService spawnService,
        IFramework framework,
        IPluginLog log)
        : this(
            new WorldActorTableAdapter(objectTable),
            gPoseService,
            actorManager,
            spawnService.CloneFromWorldSource,
            framework,
            log)
    {
    }

    internal WorldActorDiscovery(
        IWorldActorTableAdapter adapter,
        IGPoseService gPoseService,
        IActorManager actorManager,
        Func<nint, IActor?> cloneSource,
        IFramework? framework = null,
        IPluginLog? log = null)
    {
        _adapter = adapter;
        _gPose = gPoseService;
        _actorManager = actorManager;
        _cloneSource = cloneSource;
        _framework = framework;
        _log = log;
    }

    /// <summary>Object-table reads and the clone both belong to the framework
    /// (main) thread; off-thread calls refuse rather than race the game.</summary>
    private bool OnOwnerThread =>
        _framework is null || _framework.IsInFrameworkUpdateThread;

    public IReadOnlyList<WorldActorCandidate> RefreshCandidates()
    {
        _observations.Clear();
        _candidates.Clear();
        if (!OnOwnerThread || !_gPose.IsGPosing)
            return Array.Empty<WorldActorCandidate>();

        List<WorldActorObservation> kept;
        try
        {
            kept = Collect();
        }
        catch (Exception ex)
        {
            _log?.Warning(
                $"WorldActorDiscovery: enumeration failed: {ex.Message}");
            return Array.Empty<WorldActorCandidate>();
        }

        kept.Sort(static (left, right) =>
            left.DistanceFromPlayer.CompareTo(right.DistanceFromPlayer));
        foreach (var observed in kept)
        {
            var id = WorldActorCandidateId.New();
            _observations[id] = observed;
            _candidates.Add(new WorldActorCandidate(
                id,
                observed.Name,
                observed.Kind!.Value,
                observed.DistanceFromPlayer));
        }
        return _candidates.ToArray();
    }

    public WorldActorImportResult CloneCandidate(WorldActorCandidateId id) =>
        CloneCandidate(id, out _);

    /// <summary>The typed import with the spawned wrapper handed out for the
    /// caller's pending-select flow — the same handoff every other spawn row
    /// uses. The wrapper is already bound inside the spawn transaction; the
    /// scene admits it through the ordinary registry scan.</summary>
    public WorldActorImportResult CloneCandidate(
        WorldActorCandidateId id, out IActor? spawned)
    {
        spawned = null;
        if (!OnOwnerThread)
            return WorldActorImportResult.NotAvailable(
                "World-actor import runs only on the game's update thread.");
        if (!_gPose.IsGPosing)
            return WorldActorImportResult.NotAvailable(
                "Cloning a world actor works only inside GPose.");
        if (!_observations.TryGetValue(id, out var stored))
            return WorldActorImportResult.Stale(
                "That world actor is from an older listing.");

        // Revalidate the EXACT identity immediately before the spawn:
        // reference, address, index, and GameObjectId must all still agree,
        // and the eligibility that admitted the candidate must still hold.
        // Any drift — despawn, same-index replacement, hide, band entry — is
        // a typed stale refusal with no native call.
        WorldActorObservation? current;
        try
        {
            current = _adapter.Revalidate(stored);
        }
        catch (Exception ex)
        {
            _log?.Warning(
                $"WorldActorDiscovery: revalidation failed: {ex.Message}");
            current = null;
        }
        if (current is not { } fresh
            || fresh.Address != stored.Address
            || fresh.ObjectIndex != stored.ObjectIndex
            || fresh.GameObjectId != stored.GameObjectId
            || fresh.Kind is null
            || !fresh.IsDrawing
            || IsProtectedIndex(fresh.ObjectIndex))
        {
            Forget(id);
            return WorldActorImportResult.Stale(
                "That world actor is no longer there.");
        }

        IActor? clone;
        try
        {
            clone = _cloneSource(fresh.Address);
        }
        catch (Exception ex)
        {
            _log?.Error($"WorldActorDiscovery: clone failed: {ex.Message}");
            clone = null;
        }
        if (clone is null)
            return WorldActorImportResult.Failed(
                "The clone failed — GPose may be full or spawning unavailable.");
        spawned = clone;
        return WorldActorImportResult.Ok();
    }

    private List<WorldActorObservation> Collect()
    {
        var auxiliary = new HashSet<nint>();
        foreach (var aux in _actorManager.AuxiliaryActors)
            auxiliary.Add(aux.Address);

        // The three enumerations overlap in what they can show; identity
        // dedupes by address, first observation wins.
        var seen = new HashSet<nint>();
        var kept = new List<WorldActorObservation>();
        foreach (var observed in _adapter.EnumerateOverworld())
        {
            if (!IsEligible(observed, auxiliary))
                continue;
            if (!seen.Add(observed.Address))
                continue;
            kept.Add(observed);
        }
        return kept;
    }

    private static bool IsEligible(
        in WorldActorObservation observed, HashSet<nint> auxiliary) =>
        observed.Address != nint.Zero
        && observed.Kind is not null
        && observed.IsDrawing
        && !IsProtectedIndex(observed.ObjectIndex)
        && !auxiliary.Contains(observed.Address);

    /// <summary>200–439: the GPose scan band (201–439) whose occupants every
    /// Poser mutation surface assumes were admitted through the registry
    /// chain, plus slot 200 — the game's own UI-copy slot. A world-actor
    /// candidate may never name an occupant of this band; Poser's own
    /// auxiliary bodies outside it (the 441 preview) are excluded by address.</summary>
    private static bool IsProtectedIndex(ushort objectIndex) =>
        objectIndex is >= 200 and <= 439;

    private void Forget(WorldActorCandidateId id)
    {
        _observations.Remove(id);
        for (int i = 0; i < _candidates.Count; i++)
        {
            if (_candidates[i].Id == id)
            {
                _candidates.RemoveAt(i);
                return;
            }
        }
    }
}
