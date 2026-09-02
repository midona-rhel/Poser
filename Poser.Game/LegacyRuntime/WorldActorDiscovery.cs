using Poser.Domain.Actors;
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
    bool IsDrawing,
    Vector3 Position = default)
{
    /// <summary>The exact identity a candidate id is keyed by. An occupant
    /// that differs in any member is a different object and gets its own id —
    /// which is what keeps a reused id from ever meaning "whatever is at that
    /// index now".</summary>
    internal (nint, ushort, ulong) Identity =>
        (Address, ObjectIndex, GameObjectId);
}

/// <summary>
/// The read-only object-table boundary for overworld discovery. Enumeration
/// and revalidation only — no member of this seam can mutate game state.
/// </summary>
internal interface IWorldActorTableAdapter
{
    /// <summary>The player's own game object id, or 0 when there is no
    /// player: the one Player-kind actor the world may lend.</summary>
    ulong LocalPlayerId => 0;

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

    public ulong LocalPlayerId => _objectTable.LocalPlayer?.GameObjectId ?? 0;


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
            IsDrawing(gameObject.Address),
            gameObject.Position);

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
public sealed class WorldActorDiscovery : IWorldActorReadPort, IWorldActorDiscovery
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

    /// <summary>The id each exact identity was last given, so a refresh
    /// re-issues it rather than minting a new one. Rebuilt beside
    /// <see cref="_observations"/> every pass — an identity that stops being
    /// listed loses its id with it, and a DIFFERENT occupant of the same index
    /// is a different key and gets a fresh id.</summary>
    private readonly Dictionary<(nint, ushort, ulong), WorldActorCandidateId>
        _idsByIdentity = new();

    /// <summary>Scratch for the rebuild above; a field so a refresh that runs
    /// on a UI cadence does not allocate a dictionary per pass.</summary>
    private readonly Dictionary<(nint, ushort, ulong), WorldActorCandidateId>
        _reissued = new();

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
            spawnService.AdoptFromWorldSource,
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
        {
            _idsByIdentity.Clear();
            return Array.Empty<WorldActorCandidate>();
        }

        List<WorldActorObservation> kept;
        try
        {
            kept = Collect();
        }
        catch (Exception ex)
        {
            _log?.Warning(
                $"WorldActorDiscovery: enumeration failed: {ex.Message}");
            _idsByIdentity.Clear();
            return Array.Empty<WorldActorCandidate>();
        }

        kept.Sort(static (left, right) =>
            left.DistanceFromPlayer.CompareTo(right.DistanceFromPlayer));
        // The identity map is rebuilt from THIS pass, reusing the id an
        // identity already held: the listing is what keeps an id alive, so an
        // object that stopped being listed loses its id here.
        _reissued.Clear();
        foreach (var observed in kept)
        {
            var identity = observed.Identity;
            if (!_idsByIdentity.TryGetValue(identity, out var id))
                id = WorldActorCandidateId.New();
            _reissued[identity] = id;
            _observations[id] = observed;
            _candidates.Add(new WorldActorCandidate(
                id,
                observed.Name,
                observed.Kind!.Value,
                observed.DistanceFromPlayer,
                observed.Position));
        }
        _idsByIdentity.Clear();
        foreach (var pair in _reissued)
            _idsByIdentity[pair.Key] = pair.Value;
        return _candidates.ToArray();
    }

    public WorldActorImportResult CloneCandidate(WorldActorCandidateId id) =>
        CloneCandidate(id, out _);

    /// <summary>
    /// Paints, or unpaints, the game's own selection highlight on a listed
    /// overworld actor — what a hovered adoption handle marks its actor with.
    ///
    /// <para>The mechanism is Ktisis' own and verified at its call site:
    /// <c>SceneDraw.SetActorHighlight</c>
    /// (<c>Ktisis/Interface/Overlay/SceneDraw.cs:367-377</c>) casts the
    /// wrapper's address to the client-structs GameObject and calls
    /// <c>Highlight(colour)</c> on hover-enter and
    /// <c>Highlight(ObjectHighlightColor.None)</c> on leave, refusing when the
    /// object has no draw object. This is the same call, the same guard and the
    /// same pairing.</para>
    ///
    /// <para>False means the highlight was not written — a stale id, an
    /// off-thread call, or an actor with nothing drawn. A caller must read that
    /// as "no highlight is on", never as an unpaired set.</para>
    /// </summary>
    private nint _highlightedAddress;

    public unsafe bool SetHighlight(WorldActorCandidateId id, bool highlighted)
    {
        if (!OnOwnerThread)
            return false;
        // A body that was just adopted has left the listing, but its
        // highlight is still lit: turning it off goes by the address the
        // last highlight went to, whatever the listing knows now.
        nint address = _observations.TryGetValue(id, out var stored)
            ? stored.Address
            : highlighted ? nint.Zero : _highlightedAddress;
        if (address == nint.Zero)
            return false;
        try
        {
            var native =
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)
                    address;
            if (native == null || native->DrawObject == null)
                return false;
            native->Highlight(highlighted
                ? FFXIVClientStructs.FFXIV.Client.Game.Object
                    .ObjectHighlightColor.Yellow
                : FFXIVClientStructs.FFXIV.Client.Game.Object
                    .ObjectHighlightColor.None);
            _highlightedAddress = highlighted ? address : nint.Zero;
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warning(
                $"WorldActorDiscovery: highlighting a world actor failed: {ex.Message}");
            return false;
        }
    }

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
        if (!IsLendable(fresh))
        {
            Forget(id);
            return WorldActorImportResult.NotAvailable(
                "Another player's character cannot be added to the scene.");
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
                "The actor could not be added to the scene.");
        // The handle's highlight goes with the handle.
        if (_highlightedAddress == fresh.Address)
            SetHighlight(id, false);
        spawned = clone;
        return WorldActorImportResult.Ok();
    }

    private List<WorldActorObservation> Collect()
    {
        var auxiliary = new HashSet<nint>();
        foreach (var aux in _actorManager.AuxiliaryActors)
            auxiliary.Add(aux.Address);
        // A body the scene already holds — adopted by reference — is not
        // offered again.
        foreach (var held in _actorManager.Actors)
            auxiliary.Add(held.Address);

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

    private bool IsEligible(
        in WorldActorObservation observed, HashSet<nint> auxiliary) =>
        observed.Address != nint.Zero
        && observed.Kind is not null
        && observed.IsDrawing
        && !IsProtectedIndex(observed.ObjectIndex)
        && !auxiliary.Contains(observed.Address)
        && IsLendable(observed);

    /// <summary>Another player's character is never borrowed: only the
    /// player's own, and every NPC kind.</summary>
    private bool IsLendable(in WorldActorObservation observed) =>
        observed.Kind != WorldActorKind.Player
        || (observed.GameObjectId != 0 && observed.GameObjectId == _adapter.LocalPlayerId);

    /// <summary>200–439: the GPose scan band (201–439) whose occupants every
    /// Poser mutation surface assumes were admitted through the registry
    /// chain, plus slot 200 — the game's own UI-copy slot. A world-actor
    /// candidate may never name an occupant of this band; Poser's own
    /// auxiliary bodies outside it (the 441 preview) are excluded by address.</summary>
    private static bool IsProtectedIndex(ushort objectIndex) =>
        objectIndex is >= 200 and <= 439;

    private void Forget(WorldActorCandidateId id)
    {
        if (_observations.Remove(id, out var forgotten))
            _idsByIdentity.Remove(forgotten.Identity);
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
