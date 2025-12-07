using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Entities;
using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockActorManager : IActorManager
{
    private readonly List<IActor> _actors = new();
    private readonly List<IActor> _selectedActors = new();

    public IReadOnlyList<IActor> Actors => _actors.AsReadOnly();
    public IReadOnlyList<IActor> SelectedActors => _selectedActors.AsReadOnly();
    public IActor? PrimarySelectedActor => _selectedActors.FirstOrDefault();

    public void Select(IActor actor)
    {
        if (!_actors.Contains(actor)) return;
        _selectedActors.Clear();
        _selectedActors.Add(actor);
    }

    public void SelectMultiple(IEnumerable<IActor> actors)
    {
        _selectedActors.Clear();
        foreach (var actor in actors.Where(a => _actors.Contains(a)))
        {
            _selectedActors.Add(actor);
        }
    }

    public void AddToSelection(IActor actor)
    {
        if (!_actors.Contains(actor) || _selectedActors.Contains(actor)) return;
        _selectedActors.Add(actor);
    }

    public void RemoveFromSelection(IActor actor)
    {
        _selectedActors.Remove(actor);
    }

    public void ClearSelection()
    {
        _selectedActors.Clear();
    }

    public bool IsSelected(IActor actor) => _selectedActors.Contains(actor);

    public void RefreshActors()
    {
        // No-op in mock
    }

    public void Dispose()
    {
        _actors.Clear();
        _selectedActors.Clear();
    }

    /// <summary>
    /// Adds an actor to the mock list.
    /// </summary>
    public void AddActor(IActor actor)
    {
        _actors.Add(actor);
    }

    /// <summary>
    /// Clears all actors from the mock list.
    /// </summary>
    public void ClearActors()
    {
        ClearSelection();
        _actors.Clear();
    }
}
