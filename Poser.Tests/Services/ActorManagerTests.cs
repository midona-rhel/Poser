using System.Collections.Generic;
using Poser.Core;
using Poser.Entities;
using Poser.Tests.Mocks;
using Xunit;

namespace Poser.Tests.Services;

public class ActorManagerTests
{
    [Fact]
    public void Actors_DefaultValue_IsEmpty()
    {
        var manager = new MockActorManager();

        Assert.Empty(manager.Actors);
    }

    [Fact]
    public void SelectedActors_DefaultValue_IsEmpty()
    {
        var manager = new MockActorManager();

        Assert.Empty(manager.SelectedActors);
    }

    [Fact]
    public void PrimarySelectedActor_WhenNoSelection_IsNull()
    {
        var manager = new MockActorManager();

        Assert.Null(manager.PrimarySelectedActor);
    }

    [Fact]
    public void AddActor_AddsActorToList()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);

        manager.AddActor(actor);

        Assert.Single(manager.Actors);
        Assert.Contains(actor, manager.Actors);
    }

    [Fact]
    public void ClearActors_RemovesAllActors()
    {
        var manager = new MockActorManager();
        manager.AddActor(new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero));
        manager.AddActor(new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero));

        manager.ClearActors();

        Assert.Empty(manager.Actors);
    }

    [Fact]
    public void ClearActors_ClearsSelection()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);
        manager.Select(actor);

        manager.ClearActors();

        Assert.Null(manager.PrimarySelectedActor);
        Assert.Empty(manager.SelectedActors);
    }

    [Fact]
    public void Select_SetsPrimarySelectedActor()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);

        manager.Select(actor);

        Assert.Equal(actor, manager.PrimarySelectedActor);
    }

    [Fact]
    public void Select_SetsSelectedActorsList()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);

        manager.Select(actor);

        Assert.Single(manager.SelectedActors);
        Assert.Equal(actor, manager.SelectedActors[0]);
    }

    [Fact]
    public void Select_DoesNotSelectActorNotInList()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        // Don't add actor to manager

        manager.Select(actor);

        Assert.Null(manager.PrimarySelectedActor);
    }

    [Fact]
    public void AddToSelection_AddsToExistingSelection()
    {
        var manager = new MockActorManager();
        var actor1 = new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero);
        var actor2 = new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero);
        manager.AddActor(actor1);
        manager.AddActor(actor2);
        manager.Select(actor1);

        manager.AddToSelection(actor2);

        Assert.Equal(2, manager.SelectedActors.Count);
        Assert.Contains(actor1, manager.SelectedActors);
        Assert.Contains(actor2, manager.SelectedActors);
    }

    [Fact]
    public void RemoveFromSelection_RemovesFromSelection()
    {
        var manager = new MockActorManager();
        var actor1 = new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero);
        var actor2 = new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero);
        manager.AddActor(actor1);
        manager.AddActor(actor2);
        manager.SelectMultiple(new[] { actor1, actor2 });

        manager.RemoveFromSelection(actor1);

        Assert.Single(manager.SelectedActors);
        Assert.DoesNotContain(actor1, manager.SelectedActors);
        Assert.Contains(actor2, manager.SelectedActors);
    }

    [Fact]
    public void ClearSelection_ClearsAllSelections()
    {
        var manager = new MockActorManager();
        var actor1 = new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero);
        var actor2 = new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero);
        manager.AddActor(actor1);
        manager.AddActor(actor2);
        manager.SelectMultiple(new[] { actor1, actor2 });

        manager.ClearSelection();

        Assert.Empty(manager.SelectedActors);
        Assert.Null(manager.PrimarySelectedActor);
    }

    [Fact]
    public void IsSelected_ReturnsTrueForSelectedActor()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);
        manager.Select(actor);

        Assert.True(manager.IsSelected(actor));
    }

    [Fact]
    public void IsSelected_ReturnsFalseForUnselectedActor()
    {
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);

        Assert.False(manager.IsSelected(actor));
    }

    [Fact]
    public void SelectMultiple_SelectsAllActorsInList()
    {
        var manager = new MockActorManager();
        var actor1 = new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero);
        var actor2 = new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero);
        var actor3 = new ActorBase(new EntityId("test_3"), "Actor 3", nint.Zero);
        manager.AddActor(actor1);
        manager.AddActor(actor2);
        manager.AddActor(actor3);

        manager.SelectMultiple(new[] { actor1, actor2 });

        Assert.Equal(2, manager.SelectedActors.Count);
        Assert.Contains(actor1, manager.SelectedActors);
        Assert.Contains(actor2, manager.SelectedActors);
        Assert.DoesNotContain(actor3, manager.SelectedActors);
    }

    [Fact]
    public void SelectMultiple_IgnoresActorsNotInList()
    {
        var manager = new MockActorManager();
        var actor1 = new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero);
        var actor2 = new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero);
        manager.AddActor(actor1);
        // actor2 is not added to the manager

        manager.SelectMultiple(new[] { actor1, actor2 });

        Assert.Single(manager.SelectedActors);
        Assert.Contains(actor1, manager.SelectedActors);
    }
}
