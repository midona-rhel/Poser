using System.Numerics;

namespace Poser.Services;

public interface ICameraService
{
    /// <summary>
    /// Gets the current view matrix from the active camera.
    /// </summary>
    Matrix4x4 GetViewMatrix();

    /// <summary>
    /// Gets the current projection matrix from the active camera.
    /// </summary>
    Matrix4x4 GetProjectionMatrix();

    /// <summary>
    /// Gets the current camera position in world space.
    /// </summary>
    Vector3 GetCameraPosition();

    /// <summary>
    /// Converts a world position to screen coordinates.
    /// </summary>
    /// <param name="worldPos">The world position to convert.</param>
    /// <param name="screenPos">The resulting screen position.</param>
    /// <returns>True if the conversion succeeded, false if the position is off-screen.</returns>
    bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos);
}
