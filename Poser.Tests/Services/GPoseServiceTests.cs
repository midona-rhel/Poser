using Poser.Tests.Mocks;
using Xunit;

namespace Poser.Tests.Services;

public class GPoseServiceTests
{
    [Fact]
    public void IsGPosing_DefaultValue_IsFalse()
    {
        // Arrange
        var service = new MockGPoseService();

        // Act & Assert
        Assert.False(service.IsGPosing);
    }

    [Fact]
    public void EnterGPose_SetsIsGPosingToTrue()
    {
        // Arrange
        var service = new MockGPoseService();

        // Act
        service.EnterGPose();

        // Assert
        Assert.True(service.IsGPosing);
    }

    [Fact]
    public void ExitGPose_SetsIsGPosingToFalse()
    {
        // Arrange
        var service = new MockGPoseService();
        service.EnterGPose();

        // Act
        service.ExitGPose();

        // Assert
        Assert.False(service.IsGPosing);
    }

    [Fact]
    public void OnGPoseStateChanged_FiresWhenEnteringGPose()
    {
        // Arrange
        var service = new MockGPoseService();
        bool eventFired = false;
        bool? receivedState = null;
        service.OnGPoseStateChanged += state =>
        {
            eventFired = true;
            receivedState = state;
        };

        // Act
        service.EnterGPose();

        // Assert
        Assert.True(eventFired);
        Assert.True(receivedState);
    }

    [Fact]
    public void OnGPoseStateChanged_FiresWhenExitingGPose()
    {
        // Arrange
        var service = new MockGPoseService();
        service.EnterGPose();

        bool eventFired = false;
        bool? receivedState = null;
        service.OnGPoseStateChanged += state =>
        {
            eventFired = true;
            receivedState = state;
        };

        // Act
        service.ExitGPose();

        // Assert
        Assert.True(eventFired);
        Assert.False(receivedState);
    }

    [Fact]
    public void OnGPoseStateChanged_DoesNotFireWhenStateUnchanged()
    {
        // Arrange
        var service = new MockGPoseService();
        service.EnterGPose();

        int eventCount = 0;
        service.OnGPoseStateChanged += _ => eventCount++;

        // Act
        service.IsGPosing = true; // Same state

        // Assert
        Assert.Equal(0, eventCount);
    }
}
