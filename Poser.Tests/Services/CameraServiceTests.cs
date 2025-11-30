using System.Numerics;
using Poser.Tests.Mocks;
using Xunit;

namespace Poser.Tests.Services;

public class CameraServiceTests
{
    [Fact]
    public void GetViewMatrix_ReturnsConfiguredMatrix()
    {
        // Arrange
        var service = new MockCameraService();
        var expectedMatrix = Matrix4x4.CreateTranslation(1, 2, 3);
        service.ViewMatrix = expectedMatrix;

        // Act
        var result = service.GetViewMatrix();

        // Assert
        Assert.Equal(expectedMatrix, result);
    }

    [Fact]
    public void GetProjectionMatrix_ReturnsConfiguredMatrix()
    {
        // Arrange
        var service = new MockCameraService();
        var expectedMatrix = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16.0f / 9.0f, 0.1f, 1000.0f);
        service.ProjectionMatrix = expectedMatrix;

        // Act
        var result = service.GetProjectionMatrix();

        // Assert
        Assert.Equal(expectedMatrix, result);
    }

    [Fact]
    public void WorldToScreen_ReturnsConfiguredResult()
    {
        // Arrange
        var service = new MockCameraService();
        service.WorldToScreenResult = true;
        service.ScreenPosition = new Vector2(100, 200);

        // Act
        var success = service.WorldToScreen(Vector3.Zero, out var screenPos);

        // Assert
        Assert.True(success);
        Assert.Equal(new Vector2(100, 200), screenPos);
    }

    [Fact]
    public void WorldToScreen_WhenFalse_ReturnsConfiguredPosition()
    {
        // Arrange
        var service = new MockCameraService();
        service.WorldToScreenResult = false;
        service.ScreenPosition = new Vector2(0, 0);

        // Act
        var success = service.WorldToScreen(Vector3.One, out var screenPos);

        // Assert
        Assert.False(success);
        Assert.Equal(Vector2.Zero, screenPos);
    }

    [Fact]
    public void DefaultValues_AreIdentityMatrices()
    {
        // Arrange
        var service = new MockCameraService();

        // Act & Assert
        Assert.Equal(Matrix4x4.Identity, service.GetViewMatrix());
        Assert.Equal(Matrix4x4.Identity, service.GetProjectionMatrix());
    }
}
