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

    private readonly List<ActorBase> _actors = new();
    private readonly List<ActorBase> _selectedActors = new();

    public IReadOnlyList<ActorBase> Actors => _actors.AsReadOnly();
    public IReadOnlyList<ActorBase> SelectedActors => _selectedActors.AsReadOnly();
    public ActorBase? PrimarySelectedActor => _selectedActors.FirstOrDefault();

    public event Action? OnActorsChanged;
    public event Action<IReadOnlyList<ActorBase>>? OnSelectionChanged;

    public ActorManager(IObjectTable objectTable, IGPoseService gPoseService, IFramework framework)
    {
        _objectTable = objectTable;
        _gPoseService = gPoseService;
        _framework = framework;

        _gPoseService.OnGPoseStateChanged += OnGPoseStateChanged;
        _framework.Update += OnFrameworkUpdate;
    }

    public void Select(ActorBase actor)
    {
        if (!_actors.Contains(actor)) return;

        _selectedActors.Clear();
        _selectedActors.Add(actor);
        OnSelectionChanged?.Invoke(SelectedActors);
    }

    public void SelectMultiple(IEnumerable<ActorBase> actors)
    {
        _selectedActors.Clear();
        foreach (var actor in actors.Where(a => _actors.Contains(a)))
        {
            _selectedActors.Add(actor);
        }
        OnSelectionChanged?.Invoke(SelectedActors);
    }

    public void AddToSelection(ActorBase actor)
    {
        if (!_actors.Contains(actor) || _selectedActors.Contains(actor)) return;

        _selectedActors.Add(actor);
        OnSelectionChanged?.Invoke(SelectedActors);
    }

    public void RemoveFromSelection(ActorBase actor)
    {
        if (_selectedActors.Remove(actor))
        {
            OnSelectionChanged?.Invoke(SelectedActors);
        }
    }

    public void ClearSelection()
    {
        if (_selectedActors.Count > 0)
        {
            _selectedActors.Clear();
            OnSelectionChanged?.Invoke(SelectedActors);
        }
    }

    public bool IsSelected(ActorBase actor) => _selectedActors.Contains(actor);

    private void OnGPoseStateChanged(bool isGPosing)
    {
        if (isGPosing)
        {
            // Delay refresh by one frame to let actors initialize
            _framework.RunOnTick(RefreshActors);
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

        // Check for new/removed actors periodically
        var currentCount = GetGPoseCharacters().Count();
        if (currentCount != _actors.Count)
        {
            RefreshActors();
        }
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
        ClearActors();

        foreach (var gameObject in GetGPoseCharacters())
        {
            var actor = new ActorBase(
                new EntityId($"actor_{gameObject.GameObjectId}"),
                GetActorName(gameObject),
                gameObject.Address
            );
            _actors.Add(actor);
        }

        OnActorsChanged?.Invoke();
    }

    private void ClearActors()
    {
        _selectedActors.Clear();
        foreach (var actor in _actors)
        {
            actor.Dispose();
        }
        _actors.Clear();
        OnActorsChanged?.Invoke();
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
