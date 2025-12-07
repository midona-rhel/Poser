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
    public void SetGPoseState_SetsStateDirectly()
    {
        // Arrange
        var service = new MockGPoseService();

        // Act
        service.SetGPoseState(true);

        // Assert
        Assert.True(service.IsGPosing);

        // Act
        service.SetGPoseState(false);

        // Assert
        Assert.False(service.IsGPosing);
    }
}
