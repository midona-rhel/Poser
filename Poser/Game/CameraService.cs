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

        FFXIVClientStructs.FFXIV.Common.Math.Vector2 ffScreen;
        var result = camera->CameraBase.SceneCamera.WorldToScreen(worldPos, out ffScreen);
        screenPos = new Vector2(ffScreen.X, ffScreen.Y);
        return result;
    }
}
