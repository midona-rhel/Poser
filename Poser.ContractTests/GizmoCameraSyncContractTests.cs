extern alias ProductionPoser;

using System.Numerics;
using Poser.Entities;
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
        Assert.Equal(
            FreeCameraSpeed.Default * FreeCameraSpeed.NotchFactor,
            FreeCameraSpeed.Step(FreeCameraSpeed.Default, 1),
            5);

        Assert.Equal(-1f, WorldGizmo.AxisFlipSign(Vector3.UnitZ, Vector3.UnitZ));
        Assert.Equal(1f, WorldGizmo.AxisFlipSign(-Vector3.UnitZ, Vector3.UnitZ));

        var truthful = Projection(Vector3.Zero);
        var reportedElsewhere = Projection(new Vector3(-9f, -4f, 11f));
        Assert.Equal(truthful.WorldScale, reportedElsewhere.WorldScale, 4);
        Assert.True(
            Vector3.Dot(truthful.ViewDirection, reportedElsewhere.ViewDirection)
                > 0.9999f);
    }



}
