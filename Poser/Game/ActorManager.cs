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

public class ActorManager : IActorManager
{
    // GPose actors are in object table slots 201-439
    private const int GPoseStart = 201;
    private const int GPoseEnd = 439;

    private readonly IObjectTable _objectTable;
    private readonly IGPoseService _gPoseService;
    private readonly IFramework _framework;
    private readonly EventBus _eventBus;

    private readonly List<ActorBase> _actors = new();
    private readonly List<ActorBase> _selectedActors = new();

    // Track actor addresses to detect actual changes
    private readonly HashSet<nint> _lastActorAddresses = new();

    // Debounce flag to prevent multiple refreshes per frame
    private bool _pendingRefresh = false;

    public IReadOnlyList<ActorBase> Actors => _actors.AsReadOnly();
    public IReadOnlyList<ActorBase> SelectedActors => _selectedActors.AsReadOnly();
    public ActorBase? PrimarySelectedActor => _selectedActors.FirstOrDefault();

    public ActorManager(IObjectTable objectTable, IGPoseService gPoseService, IFramework framework, EventBus eventBus)
    {
        _objectTable = objectTable;
        _gPoseService = gPoseService;
        _framework = framework;
        _eventBus = eventBus;

        _gPoseService.OnGPoseStateChanged += OnGPoseStateChanged;
        _framework.Update += OnFrameworkUpdate;
    }

    public void Select(ActorBase actor)
    {
        if (!_actors.Contains(actor)) return;

        _selectedActors.Clear();
        _selectedActors.Add(actor);
        _eventBus.Publish(new SelectionChangedEvent(SelectedActors));
    }

    public void SelectMultiple(IEnumerable<ActorBase> actors)
    {
        _selectedActors.Clear();
        foreach (var actor in actors.Where(a => _actors.Contains(a)))
        {
            _selectedActors.Add(actor);
        }
        _eventBus.Publish(new SelectionChangedEvent(SelectedActors));
    }

    public void AddToSelection(ActorBase actor)
    {
        if (!_actors.Contains(actor) || _selectedActors.Contains(actor)) return;

        _selectedActors.Add(actor);
        _eventBus.Publish(new SelectionChangedEvent(SelectedActors));
    }

    public void RemoveFromSelection(ActorBase actor)
    {
        if (_selectedActors.Remove(actor))
        {
            _eventBus.Publish(new SelectionChangedEvent(SelectedActors));
        }
    }

    public void ClearSelection()
    {
        if (_selectedActors.Count > 0)
        {
            _selectedActors.Clear();
            _eventBus.Publish(new SelectionChangedEvent(SelectedActors));
        }
    }

    public bool IsSelected(ActorBase actor) => _selectedActors.Contains(actor);

    private void OnGPoseStateChanged(bool isGPosing)
    {
        if (isGPosing)
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
            if (obj != null && obj.ObjectKind == ObjectKind.Player ||
                obj?.ObjectKind == ObjectKind.BattleNpc ||
                obj?.ObjectKind == ObjectKind.EventNpc ||
                obj?.ObjectKind == ObjectKind.Companion ||
                obj?.ObjectKind == ObjectKind.Retainer)
            {
                yield return obj;
            }
        }
    }

    public void RefreshActors()
    {
        // Clear existing actors
        _selectedActors.Clear();
        foreach (var actor in _actors)
        {
            actor.Dispose();
        }
        _actors.Clear();
        _lastActorAddresses.Clear();

        // Build new actor list
        foreach (var gameObject in GetGPoseCharacters())
        {
            var actor = new ActorBase(
                new EntityId($"actor_{gameObject.GameObjectId}"),
                GetActorName(gameObject),
                gameObject.Address
            );
            _actors.Add(actor);
            _lastActorAddresses.Add(gameObject.Address);
        }

        _eventBus.Publish(new ActorListChangedEvent(Actors));
    }

    private void ClearActors()
    {
        _selectedActors.Clear();
        foreach (var actor in _actors)
        {
            actor.Dispose();
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
        _gPoseService.OnGPoseStateChanged -= OnGPoseStateChanged;
        _framework.Update -= OnFrameworkUpdate;
        ClearActors();
        GC.SuppressFinalize(this);
    }
}
