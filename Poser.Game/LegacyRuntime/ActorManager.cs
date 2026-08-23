using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Manages the lifecycle of actors in GPose.
///
/// NOTE: Selection is handled by the application SelectionSession, not here.
/// This class only tracks actor lifecycle (discovery, refresh).
/// </summary>
public class ActorManager : IActorManager
{
    /// <summary>
    /// THE actor identity formula, in one place because two call sites have to
    /// agree exactly: this one, and the spawn service's fail-closed check that
    /// a freshly bound wrapper is the one it asked for.
    ///
    /// <para>It is keyed on the GameObjectId AND the object-table index, and
    /// the index is not decoration. A GPose clone SHARES its source's
    /// GameObjectId — cloning the local player produces an actor whose id
    /// equals the player's — so the id alone is not unique among actors that
    /// coexist in the table. Two actors sharing an identity share a binding
    /// lineage and, worse, share the registry's per-actor bone keys: the
    /// second one bound overwrites the first, and every bone of the loser
    /// resolves to a BoneId that binds to the winner's bone object. The
    /// reference check then fails and the loser is bone-dead — no pose import,
    /// no overlay toggles — until something reorders the table.</para>
    ///
    /// <para>The index is unique among coexisting objects and stable for as
    /// long as an actor holds its slot, so it buys uniqueness without costing
    /// the continuity the lineage depends on. A slot genuinely reused by a
    /// different actor is a different actor, and the registry's own
    /// address-change check ages the generation for it.</para>
    /// </summary>
    internal static class ActorIdentity
    {
        public static EntityId For(IGameObject gameObject) =>
            For(gameObject.GameObjectId, gameObject.ObjectIndex);

        public static EntityId For(ulong gameObjectId, ushort objectIndex) =>
            new($"actor_{gameObjectId}_{objectIndex}");
    }

    // GPose actors are in object table slots 201-439
    private const int GPoseStart = 201;
    private const int GPoseEnd = 439;

    /// <summary>
    /// Converts Dalamud's ObjectKind to our ActorKind enum.
    /// </summary>
    private static ActorKind ToActorKind(ObjectKind objectKind) => objectKind switch
    {
        ObjectKind.Pc => ActorKind.Player,
        ObjectKind.BattleNpc => ActorKind.BattleNpc,
        ObjectKind.EventNpc => ActorKind.EventNpc,
        ObjectKind.Companion => ActorKind.Companion,
        ObjectKind.Mount => ActorKind.Mount,
        ObjectKind.Ornament => ActorKind.Ornament,
        ObjectKind.Retainer => ActorKind.Retainer,
        _ => ActorKind.None
    };

    private readonly IObjectTable _objectTable;
    private readonly IGPoseService _gPoseService;
    private readonly IFramework _framework;
    private readonly IEventBus _eventBus;
    private readonly ITargetManager _targetManager;

    private readonly List<IActor> _actors = new();

    // Bodies outside the GPose range that Poser drives itself (the CharaView
    // preview at slot 441). Registrations arrive from the draw thread; the
    // list itself is only ever rebuilt on the framework tick, like _actors.
    private readonly List<IActor> _auxiliaryActors = new();
    private readonly Dictionary<ushort, ActorKind> _auxiliaryRegistrations = new();
    private readonly object _auxiliaryGate = new();

    // Track actor addresses to detect actual changes
    private readonly HashSet<(nint Address, EntityId Id)> _lastActorIdentities = new();

    // Debounce flag to prevent multiple refreshes per frame. Written from the
    // draw thread by the auxiliary registration calls, read on the tick.
    private volatile bool _pendingRefresh = false;

    public IReadOnlyList<IActor> Actors => _actors.AsReadOnly();

    public IReadOnlyList<IActor> AuxiliaryActors => _auxiliaryActors.AsReadOnly();

    private readonly Dalamud.Plugin.Services.IPluginLog? _log;

    public ActorManager(IObjectTable objectTable, IGPoseService gPoseService, IFramework framework, IEventBus eventBus, ITargetManager targetManager, Dalamud.Plugin.Services.IPluginLog? log = null)
    {
        _log = log;
        _objectTable = objectTable;
        _gPoseService = gPoseService;
        _framework = framework;
        _eventBus = eventBus;
        _targetManager = targetManager;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (e.IsGPosing)
        {
            // Mark pending refresh - will be processed on next framework update
            // This gives actors time to initialize
            _pendingRefresh = true;
        }
        else
        {
            ClearActors();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_gPoseService.IsGPosing)
            return;

        // Process pending refresh from GPose entry
        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            var entryWatch = System.Diagnostics.Stopwatch.StartNew();
            RefreshActors();
            entryWatch.Stop();
            // The GPose-entry freeze diagnostic (#31): this is the burst
            // frame; the skeleton-created and bindings lines that follow
            // carry their own timestamps.
            _log?.Debug(
                $"GPose entry: actor refresh took " +
                $"{entryWatch.Elapsed.TotalMilliseconds:0.0}ms " +
                $"({_actors.Count} actors)");
            return;
        }

        // Check for actor changes by comparing addresses
        var currentIdentities = GetGPoseCharacterIdentities();
        if (!currentIdentities.SetEquals(_lastActorIdentities))
        {
            RefreshActors();
        }
    }

    private HashSet<(nint Address, EntityId Id)> GetGPoseCharacterIdentities()
    {
        var identities = new HashSet<(nint Address, EntityId Id)>();
        foreach (var obj in GetGPoseCharacters())
        {
            // MUST be the one identity formula. This line once restated it by
            // hand, was missed when the formula gained the object index, and
            // the mismatch made "did the actor list change?" answer yes every
            // frame — a full list rebuild and event publish per frame.
            identities.Add((obj.Address, ActorIdentity.For(obj)));
        }
        // Registered auxiliary slots participate in change detection too: the
        // CharaView body appears at 441 several frames after registration and
        // nothing else in the scene changes when it does.
        foreach (var (index, _, obj) in GetAuxiliaryObjects())
        {
            identities.Add((obj.Address, AuxiliaryId(index)));
        }
        return identities;
    }

    /// <summary>
    /// Auxiliary identity rides the object-table INDEX, never the
    /// GameObjectId: a GPose clone shares its source's GameObjectId, so an
    /// id-derived key would collide with the real actor in every store keyed
    /// by <see cref="EntityId"/> (skeleton cache, binding lineages).
    /// </summary>
    private static EntityId AuxiliaryId(ushort objectIndex) =>
        new($"actor_aux_{objectIndex}");

    private IEnumerable<(ushort Index, ActorKind Kind, IGameObject Object)> GetAuxiliaryObjects()
    {
        KeyValuePair<ushort, ActorKind>[] registrations;
        lock (_auxiliaryGate)
        {
            if (_auxiliaryRegistrations.Count == 0)
                yield break;
            registrations = _auxiliaryRegistrations.ToArray();
        }

        foreach (var (index, kind) in registrations)
        {
            var obj = _objectTable[index];
            // The CharaView body is a real Character in the object table; a
            // registered index holding anything else is not ours to bind.
            if (obj is ICharacter && obj.Address != nint.Zero)
                yield return (index, kind, obj);
        }
    }

    private IEnumerable<IGameObject> GetGPoseCharacters()
    {
        for (int i = GPoseStart; i <= GPoseEnd; i++)
        {
            var obj = _objectTable[i];
            if (obj != null && (
                obj.ObjectKind == ObjectKind.Pc ||
                obj.ObjectKind == ObjectKind.BattleNpc ||
                obj.ObjectKind == ObjectKind.EventNpc ||
                obj.ObjectKind == ObjectKind.Companion ||
                obj.ObjectKind == ObjectKind.Retainer))
            {
                yield return obj;
            }
        }
    }

    public void RefreshActors()
    {
        // Reconcile in place. Selection, history, skeleton caches, and inspector
        // sessions all hold entity references; recreating every actor because one
        // GPose object appeared or disappeared invalidates all of those references.
        var existingByAddress = _actors.ToDictionary(actor => actor.Address);
        var refreshed = new List<IActor>();
        _lastActorIdentities.Clear();

        foreach (var gameObject in GetGPoseCharacters())
        {
            var id = ActorIdentity.For(gameObject);
            IActor actor;

            if (existingByAddress.Remove(gameObject.Address, out var existing) &&
                existing.Id == id)
            {
                existing.Name = GetActorName(gameObject);
                actor = existing;
            }
            else
            {
                if (existing is IDisposable replaced)
                    replaced.Dispose();

                actor = new ActorBase(
                    id,
                    GetActorName(gameObject),
                    gameObject.Address,
                    ToActorKind(gameObject.ObjectKind));
            }

            refreshed.Add(actor);
            _lastActorIdentities.Add((gameObject.Address, id));
        }

        // Anything left in the lookup disappeared from GPose.
        foreach (var removed in existingByAddress.Values)
        {
            if (removed is IDisposable disposable)
                disposable.Dispose();
        }

        _actors.Clear();
        _actors.AddRange(refreshed);
        RefreshAuxiliaryActors();
        _eventBus.Publish(new ActorListChangedEvent(AllActors()));
    }

    /// <summary>
    /// The same reconcile as the GPose scan, over the registered auxiliary
    /// indices: an unchanged address keeps its <see cref="ActorBase"/> so
    /// skeleton caches and bindings survive; a replaced body mints a new one.
    /// </summary>
    private void RefreshAuxiliaryActors()
    {
        var existingByAddress = _auxiliaryActors.ToDictionary(actor => actor.Address);
        var refreshed = new List<IActor>();

        foreach (var (index, kind, gameObject) in GetAuxiliaryObjects())
        {
            var id = AuxiliaryId(index);
            IActor actor;

            if (existingByAddress.Remove(gameObject.Address, out var existing) &&
                existing.Id == id)
            {
                existing.Name = GetActorName(gameObject);
                actor = existing;
            }
            else
            {
                if (existing is IDisposable replaced)
                    replaced.Dispose();

                actor = new ActorBase(
                    id,
                    GetActorName(gameObject),
                    gameObject.Address,
                    kind);
            }

            refreshed.Add(actor);
            _lastActorIdentities.Add((gameObject.Address, id));
        }

        foreach (var removed in existingByAddress.Values)
        {
            if (removed is IDisposable disposable)
                disposable.Dispose();
        }

        _auxiliaryActors.Clear();
        _auxiliaryActors.AddRange(refreshed);
    }

    /// <summary>
    /// The list <see cref="ActorListChangedEvent"/> carries. Every subscriber
    /// is state maintenance keyed on presence (skeleton release, pose purge,
    /// transform-override pruning, binding refresh); auxiliary bodies belong
    /// there or the preview's own state is torn down every refresh. Nothing
    /// user-facing reads the event payload — the panes read the scene
    /// snapshot, which is built from <see cref="Actors"/> alone.
    /// </summary>
    private IReadOnlyList<IActor> AllActors()
    {
        if (_auxiliaryActors.Count == 0)
            return Actors;
        var all = new List<IActor>(_actors.Count + _auxiliaryActors.Count);
        all.AddRange(_actors);
        all.AddRange(_auxiliaryActors);
        return all;
    }

    public void RegisterAuxiliary(ushort objectIndex, ActorKind kind)
    {
        lock (_auxiliaryGate)
        {
            if (_auxiliaryRegistrations.TryGetValue(objectIndex, out var existing) &&
                existing == kind)
                return;
            _auxiliaryRegistrations[objectIndex] = kind;
        }
        // Registration is callable from the draw thread; the actual object
        // table read and actor minting ride the existing pending-refresh
        // debounce onto the framework tick.
        _pendingRefresh = true;
    }

    public void UnregisterAuxiliary(ushort objectIndex)
    {
        lock (_auxiliaryGate)
        {
            if (!_auxiliaryRegistrations.Remove(objectIndex))
                return;
        }
        _pendingRefresh = true;
    }

    public IActor? GetGPoseTarget()
    {
        var gposeTarget = _targetManager.GPoseTarget;
        if (gposeTarget == null)
            return null;

        return Actors.FirstOrDefault(a => a.Address == gposeTarget.Address);
    }

    public void SetGPoseTarget(IActor actor)
    {
        foreach (var obj in _objectTable)
        {
            if (obj.Address != actor.Address) continue;
            _targetManager.GPoseTarget = obj;
            return;
        }
    }

    private void ClearActors()
    {
        foreach (var actor in _actors)
        {
            if (actor is IDisposable disposable)
                disposable.Dispose();
        }
        _actors.Clear();

        // Registrations are the caller's to drop; the bodies behind them are
        // gone with the GPose session either way.
        foreach (var actor in _auxiliaryActors)
        {
            if (actor is IDisposable disposable)
                disposable.Dispose();
        }
        _auxiliaryActors.Clear();

        _lastActorIdentities.Clear();
        _eventBus.Publish(new ActorListChangedEvent(Actors));
    }

    /// <summary>
    /// The actor's plain game name; the object-table index names only the
    /// nameless (a fresh spawn before the game assigns one). The index used
    /// to be appended to EVERY name — a debugging crutch that leaked into
    /// auto-save file names ("Midona Rhel (201).pose", 201 being the first
    /// GPose slot) and forced every display surface to strip it back off.
    /// Identity never rode on it: actors are tracked by address and
    /// EntityId, and same-named clones are disambiguated where it matters
    /// (auto-save's per-snapshot " (2)" suffixes, the sidebar's ids).
    /// </summary>
    private static string GetActorName(IGameObject gameObject)
    {
        var name = gameObject.Name.TextValue;
        if (string.IsNullOrEmpty(name))
        {
            return $"Actor {gameObject.ObjectIndex}";
        }
        return name;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        ClearActors();
        GC.SuppressFinalize(this);
    }
}
