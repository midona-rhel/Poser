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
        // Arrange
        var manager = new MockActorManager();

        // Act & Assert
        Assert.Empty(manager.Actors);
    }

    [Fact]
    public void SelectedActor_DefaultValue_IsNull()
    {
        // Arrange
        var manager = new MockActorManager();

        // Act & Assert
        Assert.Null(manager.SelectedActor);
    }

    [Fact]
    public void AddActor_AddsActorToList()
    {
        // Arrange
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);

        // Act
        manager.AddActor(actor);

        // Assert
        Assert.Single(manager.Actors);
        Assert.Contains(actor, manager.Actors);
    }

    [Fact]
    public void AddActor_FiresOnActorsChanged()
    {
        // Arrange
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        bool eventFired = false;
        manager.OnActorsChanged += () => eventFired = true;

        // Act
        manager.AddActor(actor);

        // Assert
        Assert.True(eventFired);
    }

    [Fact]
    public void ClearActors_RemovesAllActors()
    {
        // Arrange
        var manager = new MockActorManager();
        manager.AddActor(new ActorBase(new EntityId("test_1"), "Actor 1", nint.Zero));
        manager.AddActor(new ActorBase(new EntityId("test_2"), "Actor 2", nint.Zero));

        // Act
        manager.ClearActors();

        // Assert
        Assert.Empty(manager.Actors);
    }

    [Fact]
    public void ClearActors_ClearsSelectedActor()
    {
        // Arrange
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);
        manager.SelectedActor = actor;

        // Act
        manager.ClearActors();

        // Assert
        Assert.Null(manager.SelectedActor);
    }

    [Fact]
    public void SelectedActor_FiresOnSelectedActorChanged()
    {
        // Arrange
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);

        ActorBase? receivedActor = null;
        manager.OnSelectedActorChanged += a => receivedActor = a;

        // Act
        manager.SelectedActor = actor;

        // Assert
        Assert.Equal(actor, receivedActor);
    }

    [Fact]
    public void SelectedActor_DoesNotFireEventWhenSetToSameValue()
    {
        // Arrange
        var manager = new MockActorManager();
        var actor = new ActorBase(new EntityId("test_1"), "Test Actor", nint.Zero);
        manager.AddActor(actor);
        manager.SelectedActor = actor;

        int eventCount = 0;
        manager.OnSelectedActorChanged += _ => eventCount++;

        // Act
        manager.SelectedActor = actor; // Same value

        // Assert
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void RefreshActors_FiresOnActorsChanged()
    {
        // Arrange
        var manager = new MockActorManager();
        bool eventFired = false;
        manager.OnActorsChanged += () => eventFired = true;

        // Act
        manager.RefreshActors();

        // Assert
        Assert.True(eventFired);
    }
}
