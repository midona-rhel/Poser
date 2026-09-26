using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Lights;

public sealed class LightFiles(
    IFramework framework, IEntityBindings bindings, ISceneCreation creation,
    IPlacementAnchorSource anchors, IPluginLog log) : ILightFiles
{
    public SceneCreationResult Import(string path, ObjectPlacementMode mode)
    {
        if (!framework.IsInFrameworkUpdateThread)
            return new(null, "Light import requires the framework thread.");
        if (!anchors.TryCurrentFor(mode, out var position, out var yaw, out var refusal))
            return new(null, refusal);
        try
        {
            var document = LightFile.Load(path);
            if (document is null) return new(null, "The light file could not be read.");
            if (mode != ObjectPlacementMode.AsSaved)
            {
                var anchor = mode == ObjectPlacementMode.RelativeToCamera
                    ? document.CameraAnchor : document.ActorAnchor;
                if (anchor is null)
                    return new(null, mode == ObjectPlacementMode.RelativeToCamera
                        ? "This entry records no camera anchor, so it cannot be placed relative to the camera. Load it as saved instead."
                        : "This entry records no actor anchor (nothing was selected when it was saved), so it cannot be placed relative to an actor. Load it as saved instead.");
                ObjectPlacement.Rebase(document.Transform, anchor, position, yaw);
            }
            return creation.CreateLight(document, $"Add light from {Path.GetFileNameWithoutExtension(path)}");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to import light: {ex.Message}");
            return new(null, "The light could not be imported.");
        }
    }

    public SceneActionResult Export(LightId id, string path)
    {
        if (!framework.IsInFrameworkUpdateThread)
            return SceneActionResult.Fail("Light export requires the framework thread.");
        if (bindings.Resolve(id) is not { Success: true, Value: { IsValid: true } light } ||
            bindings.GetLightId(light) != id)
            return SceneActionResult.Fail("The light no longer exists.");
        try
        {
            var document = LightDocument.Capture(light);
            document.CameraAnchor = anchors.CameraAnchorNow();
            document.ActorAnchor = anchors.ActorAnchorNow();
            if (document.Save(path)) return SceneActionResult.Ok();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to export light: {ex.Message}");
        }
        return SceneActionResult.Fail("The light file could not be written.");
    }
}
