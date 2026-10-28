using Poser.Application.Transforms;

namespace Poser.Application.Scene;

/// <summary>
/// Removes a load's groups and parent links, restores environment and camera
/// baselines, then releases its entities in reverse order within each kind.
/// Used both when a load aborts and when the user undoes a committed load.
/// </summary>
internal sealed class SceneLoadRollback(
    ISceneStatePort sceneState, ISceneMaterializer materializer, ISceneStructure structure,
    TransformParenting parenting)
{
    /// <summary>
    /// Framework thread only and idempotent — each token clears as it is
    /// released. Returns the joined failure detail, or null.
    /// </summary>
    public string? Run(SceneOperation operation)
    {
        var failures = new List<string>();

        try
        {
            structure.Remove(operation.ImportedGroups);
            operation.ImportedGroups.Clear();
        }
        catch (Exception ex)
        {
            failures.Add($"group removal: {ex.Message}");
        }

        foreach (var child in operation.ImportedLinks)
            parenting.Remove(child);
        operation.ImportedLinks.Clear();

        if (operation.EnvironmentBaseline is { } environment)
        {
            try
            {
                sceneState.ApplyEnvironment(environment);
                operation.EnvironmentBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"environment restore: {ex.Message}");
            }
        }

        if (operation.WorldBaseline is { } world)
        {
            try
            {
                sceneState.ApplyWorld(world);
                operation.WorldBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"world toggle restore: {ex.Message}");
            }
        }

        if (operation.DefaultCameraBaseline is { } camera)
        {
            try
            {
                materializer.RestoreDefaultCamera(camera);
                operation.DefaultCameraBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"default camera restore: {ex.Message}");
            }
        }

        // Release map objects before destroying other entities so cleanup failures
        // below cannot prevent restoration of borrowed objects.
        Release(operation.BorrowedWorldObjects, materializer.ReleaseWorldObject,
            "world object release", failures);
        Release(operation.CreatedCameras, materializer.DestroyCamera,
            "camera", failures);
        Release(operation.SpawnedLights, materializer.DestroyLight,
            "light", failures);
        Release(operation.StagedOverlays, materializer.DestroyOverlay,
            "overlay", failures);
        Release(operation.SpawnedProps, materializer.DestroyProp,
            "object", failures);
        Release(operation.SpawnedActors, materializer.DestroyActor,
            "actor", failures);

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private static void Release(
        List<SceneEntityHandle> tokens,
        Action<SceneEntityHandle> destroy,
        string kind,
        List<string> failures)
    {
        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            try
            {
                destroy(tokens[index]);
                tokens.RemoveAt(index);
            }
            catch (Exception ex)
            {
                failures.Add($"{kind} destruction: {ex.Message}");
            }
        }
    }
}
