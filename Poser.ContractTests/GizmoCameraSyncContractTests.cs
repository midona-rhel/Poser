extern alias ProductionPoser;

using System.Numerics;
using Poser.Services;
using ProductionPoser::Poser.UI.Controls;

namespace Poser.ContractTests;

/// <summary>
/// The world gizmo's geometry belongs to the matrix it draws through, and to
/// nothing else. A free camera replaces the rendered view matrix while the
/// game goes on orbiting its own camera under the native input a free camera
/// does not consume — so a camera position sourced beside the view matrix
/// drifts away from the picture, and every quantity measured from it drifts
/// with it: the gizmo resizes and the drag planes move while the shot on
/// screen stands perfectly still. These tests hold the projection to its own
/// matrix by feeding it a camera whose reported position LIES, which is
/// exactly the shape of the live defect.
/// </summary>
public sealed class GizmoCameraSyncContractTests
{
    /// <summary>A camera whose view matrix and reported position disagree:
    /// the frame renders from <paramref name="rendered"/>, while
    /// <c>GetCameraPosition</c> answers <paramref name="reported"/> — the
    /// live free-camera desync, in a fake.</summary>
    private sealed class DesyncedCamera(
        Vector3 rendered, Vector3 target, Vector3 reported) : ICameraService
    {
        public Matrix4x4 GetViewMatrix() =>
            Matrix4x4.CreateLookAt(rendered, target, Vector3.UnitY);

        public Matrix4x4 GetProjectionMatrix() =>
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f, 16f / 9f, 0.1f, 100f);

        public Vector3 GetCameraPosition() => reported;

        public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
        {
            screenPos = default;
            return false;
        }

        public Vector3 ScreenToWorld(Vector2 screenPos, float depth) => default;

        public float GetDepthToPosition(Vector3 worldPos) =>
            Vector3.Distance(reported, worldPos);

        public Vector3 GetLookDirection() =>
            Vector3.Normalize(target - rendered);
    }

    private static readonly Vector3 Rendered = new(0f, 2f, 6f);
    private static readonly Vector3 Pivot = new(0.4f, 0.1f, -0.2f);

    private static WorldGizmoProjection Projection(Vector3 reported)
    {
        var projection = WorldGizmoProjection.Create(
            new DesyncedCamera(Rendered, Vector3.Zero, reported),
            new Vector2(1920f, 1080f),
            Pivot,
            80f);
        Assert.NotNull(projection);
        return projection!;
    }

    [Fact]
    public void Camera_position_comes_from_the_rendered_view_matrix()
    {
        // The orbit camera has swung far away while the free camera holds the
        // shot. The projection must report the eye it actually draws through.
        var projection = Projection(new Vector3(-9f, -4f, 11f));
        Assert.True(
            Vector3.Distance(projection.CameraPosition, Rendered) < 1e-3f,
            $"Camera position {projection.CameraPosition} followed the "
                + "reported position instead of the view matrix.");
    }

    [Fact]
    public void Gizmo_scale_does_not_move_when_only_the_reported_position_does()
    {
        // The reported defect, measured: the shot is identical in all three
        // cases, so the handle size must be identical too. WorldScale is the
        // world length that projects to the requested pixel size — it is the
        // single number the whole handle geometry is built from.
        float truthful = Projection(Rendered).WorldScale;
        foreach (var drifted in new[]
        {
            new Vector3(-9f, -4f, 11f),
            new Vector3(6f, 6f, -6f),
            Vector3.Zero,
        })
        {
            Assert.Equal(truthful, Projection(drifted).WorldScale, 4);
        }
    }

    [Fact]
    public void View_direction_does_not_move_when_only_the_reported_position_does()
    {
        var truthful = Projection(Rendered).ViewDirection;
        var drifted = Projection(new Vector3(6f, 6f, -6f)).ViewDirection;
        Assert.True(
            Vector3.Dot(truthful, drifted) > 0.9999f,
            "View direction followed the reported position.");
    }

    [Fact]
    public void Drag_plane_rays_stay_anchored_to_the_rendered_camera()
    {
        // A ray cast through the pivot's own screen point onto the pivot's
        // view plane must land back on the pivot. The direction is unprojected
        // from the view matrix, so an origin taken from anywhere else puts the
        // hit somewhere the user did not click — a translate drag that slides
        // off under a camera the user never moved.
        var projection = Projection(new Vector3(-9f, -4f, 11f));
        var hit = projection.RayPlane(
            projection.Center, Pivot, projection.ViewDirection);
        Assert.NotNull(hit);
        Assert.True(
            Vector3.Distance(hit!.Value, Pivot) < 1e-2f,
            $"Ray/plane hit {hit} missed the pivot {Pivot}.");
    }
}
