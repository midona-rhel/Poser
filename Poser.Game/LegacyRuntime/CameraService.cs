using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Services;

namespace Poser.Game;

public class CameraService : ICameraService
{
    public unsafe Matrix4x4 GetViewMatrix()
    {
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return Matrix4x4.Identity;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return Matrix4x4.Identity;

        var viewMatrix = camera->CameraBase.SceneCamera.ViewMatrix;
        viewMatrix.M44 = 1;
        return viewMatrix;
    }

    public unsafe Matrix4x4 GetProjectionMatrix()
    {
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return Matrix4x4.Identity;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return Matrix4x4.Identity;

        var renderCamera = camera->CameraBase.SceneCamera.RenderCamera;
        if (renderCamera == null)
            return Matrix4x4.Identity;
        var proj = renderCamera->ProjectionMatrix;
        proj.M33 = -(renderCamera->FarPlane + renderCamera->NearPlane) / (renderCamera->FarPlane - renderCamera->NearPlane);
        proj.M43 = -(2f * renderCamera->FarPlane * renderCamera->NearPlane) / (renderCamera->FarPlane - renderCamera->NearPlane);

        return proj;
    }

    /// <summary>
    /// The camera position the frame is actually RENDERED from: the inverse
    /// of the view matrix, Brio's CameraExtensions.GetPosition. Never the
    /// scene camera's Position field — a free camera replaces the view matrix
    /// while the game keeps orbiting that field under the native input a free
    /// camera does not consume (only the right-drag look and the movement
    /// keys are eaten), so the field is a second, drifting camera that
    /// nothing renders. Everything sized or aimed from "the camera" reads
    /// this, or it silently tracks the wrong one.
    /// </summary>
    public Vector3 GetCameraPosition()
    {
        var view = GetViewMatrix();
        return Matrix4x4.Invert(view, out var inverted)
            ? inverted.Translation
            : Vector3.Zero;
    }

    /// <summary>
    /// The batched half of <see cref="WorldToScreen"/>: the SAME camera
    /// derivation and the SAME viewport read, resolved once for the whole
    /// frame instead of once per projected point.
    /// </summary>
    public unsafe bool TryBeginProjection(out ScreenProjection projection)
    {
        projection = default;
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return false;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return false;

        var sceneCamera = camera->CameraBase.SceneCamera;
        var viewMatrix = sceneCamera.ViewMatrix;
        viewMatrix.M44 = 1f;

        var renderCamera = sceneCamera.RenderCamera;
        if (renderCamera == null)
            return false;

        projection = ScreenProjection.FromMatrix(
            viewMatrix * renderCamera->ProjectionMatrix,
            Dalamud.Bindings.ImGui.ImGui.GetIO().DisplaySize);
        return true;
    }

    public unsafe bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
    {
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
        {
            screenPos = Vector2.Zero;
            return false;
        }

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
        {
            screenPos = Vector2.Zero;
            return false;
        }

        // Use Ktisis-style projection that only filters behind-camera, not off-screen
        var sceneCamera = camera->CameraBase.SceneCamera;
        var viewMatrix = sceneCamera.ViewMatrix;
        viewMatrix.M44 = 1f;

        var renderCamera = sceneCamera.RenderCamera;
        if (renderCamera == null)
        {
            screenPos = Vector2.Zero;
            return false;
        }

        var matrix = viewMatrix * renderCamera->ProjectionMatrix;
        return WorldToScreenDepth(matrix, worldPos, out screenPos);
    }

    private static bool WorldToScreenDepth(Matrix4x4 m, Vector3 v, out Vector2 screenPos)
    {
        var x = (m.M11 * v.X) + (m.M21 * v.Y) + (m.M31 * v.Z) + m.M41;
        var y = (m.M12 * v.X) + (m.M22 * v.Y) + (m.M32 * v.Z) + m.M42;
        var w = (m.M14 * v.X) + (m.M24 * v.Y) + (m.M34 * v.Z) + m.M44;

        var io = Dalamud.Bindings.ImGui.ImGui.GetIO();
        var camX = io.DisplaySize.X / 2f;
        var camY = io.DisplaySize.Y / 2f;

        screenPos = new Vector2(
            camX + (camX * x / w),
            camY - (camY * y / w)
        );

        // Only reject if behind camera (w <= 0)
        return w > 0.001f;
    }

    public unsafe Vector3 ScreenToWorld(Vector2 screenPos, float depth)
    {
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return Vector3.Zero;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return Vector3.Zero;

        var sceneCamera = camera->CameraBase.SceneCamera;
        var renderCamera = sceneCamera.RenderCamera;
        if (renderCamera == null)
            return Vector3.Zero;

        var io = Dalamud.Bindings.ImGui.ImGui.GetIO();
        var displaySize = io.DisplaySize;

        // Convert screen pos to normalized device coordinates (-1 to 1)
        float ndcX = (2f * screenPos.X / displaySize.X) - 1f;
        float ndcY = 1f - (2f * screenPos.Y / displaySize.Y);

        // Get inverse view-projection matrix
        var viewMatrix = sceneCamera.ViewMatrix;
        viewMatrix.M44 = 1f;
        var projMatrix = renderCamera->ProjectionMatrix;
        var viewProj = viewMatrix * projMatrix;

        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
            return Vector3.Zero;

        // Unproject to get ray direction
        var nearPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), invViewProj);
        if (MathF.Abs(nearPoint.W) < 0.0001f)
            return Vector3.Zero;
        nearPoint /= nearPoint.W;

        var farPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), invViewProj);
        if (MathF.Abs(farPoint.W) < 0.0001f)
            return Vector3.Zero;
        farPoint /= farPoint.W;

        var rayVec = new Vector3(farPoint.X - nearPoint.X, farPoint.Y - nearPoint.Y, farPoint.Z - nearPoint.Z);
        var rayLength = rayVec.Length();
        if (rayLength < 0.0001f)
            return Vector3.Zero;

        var rayDir = rayVec / rayLength;
        // The ray was unprojected through the rendered view matrix, so its
        // origin must be that matrix's camera too — not the scene camera's
        // Position field, which a live free camera leaves behind.
        var cameraPos = GetCameraPosition();

        // Return point at specified depth along the ray
        return cameraPos + rayDir * depth;
    }

    public unsafe float GetDepthToPosition(Vector3 worldPos)
    {
        var cameraPos = GetCameraPosition();
        return Vector3.Distance(cameraPos, worldPos);
    }

    public Vector3 GetLookDirection()
    {
        // The centre-screen ray IS the look direction, and unprojection is
        // convention-free: near-to-far in clip space is toward positive w,
        // which the in-front test above already defines as "ahead".
        var io = Dalamud.Bindings.ImGui.ImGui.GetIO();
        var center = new Vector2(io.DisplaySize.X / 2f, io.DisplaySize.Y / 2f);
        var probe = ScreenToWorld(center, 1f);
        if (probe == Vector3.Zero)
            return Vector3.Zero;
        var direction = probe - GetCameraPosition();
        float length = direction.Length();
        return length < 0.0001f || !float.IsFinite(length)
            ? Vector3.Zero
            : direction / length;
    }
}
