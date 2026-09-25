using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Cameras;

public sealed class CameraFiles(
    IFramework framework,
    IEntityBindings bindings,
    ISceneCreation creation,
    IPlacementAnchorSource anchors,
    IPluginLog log) : ICameraFiles
{
    public SceneCreationResult Import(string path)
    {
        if (!framework.IsInFrameworkUpdateThread)
            return new(null, "Camera import requires the framework thread.");
        try
        {
            var document = CameraFile.Load(path);
            return document is null
                ? new(null, "The camera file could not be read.")
                : creation.CreateCamera(document, $"Add camera from {Path.GetFileNameWithoutExtension(path)}");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to import camera: {ex.Message}");
            return new(null, "The camera could not be imported.");
        }
    }

    public SceneActionResult Export(CameraId id, string path)
    {
        if (!framework.IsInFrameworkUpdateThread)
            return SceneActionResult.Fail("Camera export requires the framework thread.");
        var resolved = bindings.Resolve(id);
        if (!resolved.Success || resolved.Value is not { IsValid: true } camera ||
            bindings.GetCameraId(camera) != id)
            return SceneActionResult.Fail("The camera no longer exists.");
        try
        {
            var document = CameraDocument.Capture(camera);
            document.CameraAnchor = anchors.CameraAnchorNow();
            document.ActorAnchor = anchors.ActorAnchorNow();
            if (document.Save(path)) return SceneActionResult.Ok();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to export camera: {ex.Message}");
        }
        return SceneActionResult.Fail("The camera file could not be written.");
    }
}
