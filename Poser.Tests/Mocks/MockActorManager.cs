using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Entities;
using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockActorManager : IActorManager
{
    private readonly List<ActorBase> _actors = new();
    private readonly List<ActorBase> _selectedActors = new();

    public IReadOnlyList<ActorBase> Actors => _actors.AsReadOnly();
    public IReadOnlyList<ActorBase> SelectedActors => _selectedActors.AsReadOnly();
    public ActorBase? PrimarySelectedActor => _selectedActors.FirstOrDefault();

    public event Action? OnActorsChanged;
    public event Action<IReadOnlyList<ActorBase>>? OnSelectionChanged;

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

    public void RefreshActors()
    {
        OnActorsChanged?.Invoke();
    }

    public void Dispose()
    {
        _actors.Clear();
        _selectedActors.Clear();
    }

    /// <summary>
    /// Adds an actor to the mock list.
    /// </summary>
    public void AddActor(ActorBase actor)
    {
        _actors.Add(actor);
        OnActorsChanged?.Invoke();
    }

    /// <summary>
    /// Clears all actors from the mock list.
    /// </summary>
    public void ClearActors()
    {
        ClearSelection();
        _actors.Clear();
        OnActorsChanged?.Invoke();
    }
}
