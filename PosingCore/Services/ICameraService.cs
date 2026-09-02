using System.Numerics;

namespace Poser.Services;

/// <summary>The camera's view-projection and the display centre for one
/// frame: a surface fetches it once and projects every point with pure
/// math, instead of two interop calls per point (traced 2026-09-03).</summary>
public readonly record struct ScreenProjection(Matrix4x4 ViewProjection, Vector2 Center)
{
    /// <summary>Projects a world point; false behind the camera.</summary>
    public bool Project(Vector3 v, out Vector2 screen)
    {
        var m = ViewProjection;
        float x = (m.M11 * v.X) + (m.M21 * v.Y) + (m.M31 * v.Z) + m.M41;
        float y = (m.M12 * v.X) + (m.M22 * v.Y) + (m.M32 * v.Z) + m.M42;
        float w = (m.M14 * v.X) + (m.M24 * v.Y) + (m.M34 * v.Z) + m.M44;
        screen = new Vector2(
            Center.X + (Center.X * x / w),
            Center.Y - (Center.Y * y / w));
        return w > 0.001f;
    }
}

public interface ICameraService
{
    /// <summary>This frame's projection, for a surface that projects many
    /// points. False when the camera is not available.</summary>
    bool TryGetProjection(out ScreenProjection projection)
    {
        projection = default;
        return false;
    }

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
