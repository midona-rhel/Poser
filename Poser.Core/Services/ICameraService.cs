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

    /// <summary>
    /// Converts screen coordinates to a world position at a specific depth from the camera.
    /// </summary>
    /// <param name="screenPos">The screen position (in pixels).</param>
    /// <param name="depth">The distance from the camera.</param>
    /// <returns>The world position.</returns>
    Vector3 ScreenToWorld(Vector2 screenPos, float depth);

    /// <summary>
    /// Gets the distance from the camera to a world position.
    /// </summary>
    float GetDepthToPosition(Vector3 worldPos);

    /// <summary>
    /// The camera's world-space look direction, derived from the
    /// centre-screen unprojection ray rather than a view-matrix sign
    /// convention. Normalized; Zero when no camera is active.
    /// </summary>
    Vector3 GetLookDirection();
}
