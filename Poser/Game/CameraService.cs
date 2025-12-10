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

    public unsafe Vector3 GetCameraPosition()
    {
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return Vector3.Zero;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return Vector3.Zero;

        return camera->CameraBase.SceneCamera.Position;
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
}
