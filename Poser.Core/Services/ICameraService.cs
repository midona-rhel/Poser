using System.Numerics;

namespace Poser.Services;

/// <summary>
/// ONE frame's world→screen projection, resolved once and then applied to
/// many points without touching the game or ImGui again.
///
/// <para><see cref="ICameraService.WorldToScreen"/> re-derives everything per
/// point: the camera manager, the active camera, the render camera, the
/// view×projection product, AND an <c>ImGui.GetIO()</c> interop call for the
/// viewport size. That is correct for the handful of one-off projections a
/// pane makes, and wrong for the overlay, which projects every bone of every
/// actor on every frame — a few hundred to a few thousand rederivations and
/// the same number of interop calls to answer one unchanging question.</para>
///
/// <para>The maths and the behind-camera rejection are the same in both
/// paths; only the frequency of the lookup changes.</para>
/// </summary>
public readonly struct ScreenProjection
{
    private readonly ICameraService? _service;
    private readonly Matrix4x4 _viewProjection;
    private readonly float _halfWidth;
    private readonly float _halfHeight;

    private ScreenProjection(
        Matrix4x4 viewProjection,
        float halfWidth,
        float halfHeight)
    {
        _service = null;
        _viewProjection = viewProjection;
        _halfWidth = halfWidth;
        _halfHeight = halfHeight;
        IsResolved = true;
    }

    private ScreenProjection(ICameraService service)
    {
        _service = service;
        _viewProjection = Matrix4x4.Identity;
        _halfWidth = 0f;
        _halfHeight = 0f;
        IsResolved = true;
    }

    /// <summary>False only for the default (never-began) value.</summary>
    public bool IsResolved { get; }

    /// <summary>The batched form: one resolved view×projection and one
    /// viewport half-size, applied per point in pure managed maths.</summary>
    public static ScreenProjection FromMatrix(
        Matrix4x4 viewProjection,
        Vector2 viewportSize) =>
        new(viewProjection, viewportSize.X / 2f, viewportSize.Y / 2f);

    /// <summary>The fallback form for a service that cannot state a frame
    /// matrix: every point goes back through
    /// <see cref="ICameraService.WorldToScreen"/>, so the answer is
    /// identical and only the batching is lost.</summary>
    public static ScreenProjection PerPoint(ICameraService service) =>
        new(service);

    /// <summary>Projects one world point. Returns false for a point behind
    /// the camera — the same and ONLY rejection
    /// <see cref="ICameraService.WorldToScreen"/> makes, so an off-screen
    /// point still projects.</summary>
    public bool Project(Vector3 world, out Vector2 screen)
    {
        if (_service is { } service)
            return service.WorldToScreen(world, out screen);
        if (!IsResolved)
        {
            // The never-began value stands for "no active camera", which is
            // exactly what WorldToScreen answers in that state.
            screen = Vector2.Zero;
            return false;
        }

        ref readonly var m = ref _viewProjection;
        var x = (m.M11 * world.X) + (m.M21 * world.Y) + (m.M31 * world.Z) + m.M41;
        var y = (m.M12 * world.X) + (m.M22 * world.Y) + (m.M32 * world.Z) + m.M42;
        var w = (m.M14 * world.X) + (m.M24 * world.Y) + (m.M34 * world.Z) + m.M44;

        screen = new Vector2(
            _halfWidth + (_halfWidth * x / w),
            _halfHeight - (_halfHeight * y / w));
        return w > 0.001f;
    }
}

public interface ICameraService
{
    /// <summary>
    /// Resolves this frame's projection ONCE for a caller that is about to
    /// project many points. False when no camera is active, which is the
    /// same condition that makes every
    /// <see cref="WorldToScreen"/> call fail.
    ///
    /// <para>The default implementation keeps every existing implementor
    /// compiling and correct by handing back the per-point form; only a
    /// service that can state a frame matrix overrides it.</para>
    /// </summary>
    bool TryBeginProjection(out ScreenProjection projection)
    {
        projection = ScreenProjection.PerPoint(this);
        return true;
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
