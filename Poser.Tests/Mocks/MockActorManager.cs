using System;
using System.Collections.Generic;
using Poser.Entities;
using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockActorManager : IActorManager
{
    private readonly List<ActorBase> _actors = new();
    private ActorBase? _selectedActor;

    public IReadOnlyList<ActorBase> Actors => _actors.AsReadOnly();

    public ActorBase? SelectedActor
    {
        get => _selectedActor;
        set
        {
            if (_selectedActor != value)
            {
                _selectedActor = value;
                OnSelectedActorChanged?.Invoke(value);
            }
        }
    }

    public event Action? OnActorsChanged;
    public event Action<ActorBase?>? OnSelectedActorChanged;

    public void RefreshActors()
    {
        OnActorsChanged?.Invoke();
    }

    public void Dispose()
    {
        _actors.Clear();
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
        SelectedActor = null;
        _actors.Clear();
        OnActorsChanged?.Invoke();
    }
}
