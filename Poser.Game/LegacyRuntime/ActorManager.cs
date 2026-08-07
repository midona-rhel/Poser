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

    // Track actor addresses to detect actual changes
    private readonly HashSet<(nint Address, EntityId Id)> _lastActorIdentities = new();

    // Debounce flag to prevent multiple refreshes per frame
    private bool _pendingRefresh = false;

    public IReadOnlyList<IActor> Actors => _actors.AsReadOnly();

    public ActorManager(IObjectTable objectTable, IGPoseService gPoseService, IFramework framework, IEventBus eventBus, ITargetManager targetManager)
    {
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
            RefreshActors();
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
            identities.Add((obj.Address, new EntityId($"actor_{obj.GameObjectId}")));
        }
        return identities;
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
            var id = new EntityId($"actor_{gameObject.GameObjectId}");
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
        _eventBus.Publish(new ActorListChangedEvent(Actors));
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
