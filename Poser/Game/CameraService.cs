using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Services;

namespace Poser.Game;

public class CameraService : ICameraService
{
    public unsafe Matrix4x4 GetViewMatrix()
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        if (camera == null)
            return Matrix4x4.Identity;

        var viewMatrix = camera->CameraBase.SceneCamera.ViewMatrix;
        viewMatrix.M44 = 1;
        return viewMatrix;
    }

    public unsafe Matrix4x4 GetProjectionMatrix()
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        if (camera == null)
            return Matrix4x4.Identity;

        var renderCamera = camera->CameraBase.SceneCamera.RenderCamera;
        var proj = renderCamera->ProjectionMatrix;
        proj.M33 = -(renderCamera->FarPlane + renderCamera->NearPlane) / (renderCamera->FarPlane - renderCamera->NearPlane);
        proj.M43 = -(2f * renderCamera->FarPlane * renderCamera->NearPlane) / (renderCamera->FarPlane - renderCamera->NearPlane);

        return proj;
    }

    public unsafe Vector3 GetCameraPosition()
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        if (camera == null)
            return Vector3.Zero;

        return camera->CameraBase.SceneCamera.Position;
    }

    public unsafe bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
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
