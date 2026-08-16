extern alias ProductionPoser;

using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Transforms;
using Poser.Services;
using ProductionPoser::Poser.UI.Controls;

namespace Poser.ContractTests;

/// <summary>Contracts for Move-centre and Alt-uniform gestures.</summary>
public sealed class GizmoGestureContractTests
{
    private sealed class FakeCamera(Vector3 position, Vector3 target) : ICameraService
    {
        public Matrix4x4 GetViewMatrix() =>
            Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);

        public Matrix4x4 GetProjectionMatrix() =>
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f, 16f / 9f, 0.1f, 100f);

        public Vector3 GetCameraPosition() => position;

        public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
        {
            screenPos = default;
            return false;
        }

        public Vector3 ScreenToWorld(Vector2 screenPos, float depth) => default;

        public float GetDepthToPosition(Vector3 worldPos) =>
            Vector3.Distance(position, worldPos);

        public Vector3 GetLookDirection() =>
            Vector3.Normalize(target - position);
    }

    [Fact]
    public void Move_layout_resolves_the_centre_to_a_camera_plane_handle()
    {
        var pivot = new Vector3(0.4f, 0.1f, -0.2f);
        var projection = WorldGizmoProjection.Create(
            new FakeCamera(new Vector3(0f, 2f, 6f), Vector3.Zero),
            new Vector2(1920f, 1080f), pivot, 80f);
        Assert.NotNull(projection);

        var layout = WorldGizmo.Build(
            projection!, TransformTool.Move,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);
        var hit = WorldGizmo.HitTest(layout, projection!.Center, 8f);

        Assert.True(layout.TranslateCenterActive);
        Assert.NotNull(hit);
        Assert.Equal(WorldHandleKind.TranslateCenter, hit!.Value.Handle.Kind);

        var universal = WorldGizmo.Build(
            projection, TransformTool.Universal,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);
        var universalHit = WorldGizmo.HitTest(
            universal, projection.Center, 8f);
        Assert.NotNull(universalHit);
        Assert.Equal(WorldHandleKind.ScaleUniform,
            universalHit!.Value.Handle.Kind);

        var previous = pivot;
        var accumulated = Vector3.Zero;
        foreach (var offset in new[]
        {
            new Vector2(96f, -54f),
            new Vector2(142f, -18f),
        })
        {
            var planeHit = projection.RayPlane(
                projection.Center + offset, pivot, projection.ViewDirection);
            Assert.NotNull(planeHit);
            var step = WorldGizmo.TranslationStep(
                WorldHandleKind.TranslateCenter,
                planeHit!.Value, previous, Vector3.UnitX);
            accumulated += step;
            previous = planeHit.Value;
            Assert.Equal(0f,
                Vector3.Dot(accumulated, projection.ViewDirection), 4);

            var snapped = WorldGizmo.TranslationFromFrozenPlane(
                Vector3.Zero, planeHit.Value, pivot, Matrix4x4.Identity);
            Assert.Equal(0f,
                Vector3.Dot(snapped, projection.ViewDirection), 4);
        }
    }

    [Fact]
    public void Alt_scale_factor_preserves_frozen_component_ratios_across_updates()
    {
        var start = new Vector3(2f, 3f, 5f);
        var factors = new[] { 1.25f, 0.8f, 1.75f };

        foreach (var factor in factors)
        {
            Assert.Equal(1, WorldGizmo.ScaleAxisForModifier(1, false));
            Assert.Equal(-1, WorldGizmo.ScaleAxisForModifier(1, true));
            var scaled = WorldGizmo.ApplyUniformScale(start, factor);
            Assert.Equal(start.X / start.Y, scaled.X / scaled.Y, 4);
            Assert.Equal(start.X / start.Z, scaled.X / scaled.Z, 4);
            Assert.Equal(start * factor, scaled);
        }
    }

    [Fact]
    public void Center_updates_dispatch_plane_bound_translation_statefully()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        app.Runtime.Seed(TestStates.At(target, 0f));
        var begin = app.Gestures.Begin(new BeginTransformGesture(
            new[] { target }, TransformOperation.Translate,
            TransformSpace.World, PivotMode.PerTarget));
        Assert.True(begin.Success);

        var pivot = Vector3.Zero;
        var projection = WorldGizmoProjection.Create(
            new FakeCamera(new Vector3(0f, 2f, 6f), Vector3.Zero),
            new Vector2(1920f, 1080f), pivot, 80f);
        Assert.NotNull(projection);
        var previous = pivot;
        foreach (var offset in new[]
        {
            new Vector2(96f, -54f),
            new Vector2(142f, -18f),
        })
        {
            var hit = projection!.RayPlane(
                projection.Center + offset, pivot, projection.ViewDirection);
            Assert.NotNull(hit);
            var step = WorldGizmo.TranslationStep(
                WorldHandleKind.TranslateCenter,
                hit!.Value, previous, Vector3.UnitX);
            previous = hit.Value;
            Assert.True(app.Gestures.Update(
                begin.GestureId!.Value,
                new TransformDelta(step, Quaternion.Identity, Vector3.One)).Success);
        }

        var state = app.Runtime.State(target).Transform.Position;
        Assert.Equal(0f, Vector3.Dot(state, projection!.ViewDirection), 4);
    }
}
