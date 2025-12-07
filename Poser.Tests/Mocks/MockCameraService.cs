using System.Numerics;
using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockCameraService : ICameraService
{
    public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 ProjectionMatrix { get; set; } = Matrix4x4.Identity;
    public Vector3 CameraPosition { get; set; } = Vector3.Zero;
    public bool WorldToScreenResult { get; set; } = true;
    public Vector2 ScreenPosition { get; set; } = Vector2.Zero;

    public Matrix4x4 GetViewMatrix() => ViewMatrix;

    public Matrix4x4 GetProjectionMatrix() => ProjectionMatrix;

    public Vector3 GetCameraPosition() => CameraPosition;

    public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
    {
        screenPos = ScreenPosition;
        return WorldToScreenResult;
    }
}
