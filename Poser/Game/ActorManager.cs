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
/// NOTE: Selection is handled by SelectionService, not here.
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
        ObjectKind.Player => ActorKind.Player,
        ObjectKind.BattleNpc => ActorKind.BattleNpc,
        ObjectKind.EventNpc => ActorKind.EventNpc,
        ObjectKind.Companion => ActorKind.Companion,
        ObjectKind.MountType => ActorKind.Mount,
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
    private readonly HashSet<nint> _lastActorAddresses = new();

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
        var currentAddresses = GetGPoseCharacterAddresses();
        if (!currentAddresses.SetEquals(_lastActorAddresses))
        {
            RefreshActors();
        }
    }

    private HashSet<nint> GetGPoseCharacterAddresses()
    {
        var addresses = new HashSet<nint>();
        foreach (var obj in GetGPoseCharacters())
        {
            addresses.Add(obj.Address);
        }
        return addresses;
    }

    private IEnumerable<IGameObject> GetGPoseCharacters()
    {
        for (int i = GPoseStart; i <= GPoseEnd; i++)
        {
            var obj = _objectTable[i];
            if (obj != null && (
                obj.ObjectKind == ObjectKind.Player ||
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
        // Clear existing actors
        foreach (var actor in _actors)
        {
            if (actor is IDisposable disposable)
                disposable.Dispose();
        }
        _actors.Clear();
        _lastActorAddresses.Clear();

        // Build new actor list
        foreach (var gameObject in GetGPoseCharacters())
        {
            var actor = new ActorBase(
                new EntityId($"actor_{gameObject.GameObjectId}"),
                GetActorName(gameObject),
                gameObject.Address,
                ToActorKind(gameObject.ObjectKind)
            );
            _actors.Add(actor);
            _lastActorAddresses.Add(gameObject.Address);
        }

        _eventBus.Publish(new ActorListChangedEvent(Actors));
    }

    public IActor? GetGPoseTarget()
    {
        var gposeTarget = _targetManager.GPoseTarget;
        if (gposeTarget == null)
            return null;

        return Actors.FirstOrDefault(a => a.Address == gposeTarget.Address);
    }

    private void ClearActors()
    {
        foreach (var actor in _actors)
        {
            if (actor is IDisposable disposable)
                disposable.Dispose();
        }
        _actors.Clear();
        _lastActorAddresses.Clear();
        _eventBus.Publish(new ActorListChangedEvent(Actors));
    }

    private static string GetActorName(IGameObject gameObject)
    {
        var name = gameObject.Name.TextValue;
        if (string.IsNullOrEmpty(name))
        {
            return $"Actor {gameObject.ObjectIndex}";
        }
        return $"{name} ({gameObject.ObjectIndex})";
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        ClearActors();
        GC.SuppressFinalize(this);
    }
}
