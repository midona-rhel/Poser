using Poser.Application.Transforms;

namespace Poser.Application.Scene;

/// <summary>
/// Reverse-order destruction of everything ONE load created, plus the
/// environment and default-camera baseline restores. It is both the load's
/// abort path and its committed step's undo.
/// </summary>
internal sealed class SceneLoadRollback(
    ISceneRuntime runtime, ISceneStructure structure, TransformParenting parenting)
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
                runtime.ApplyEnvironment(environment);
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
                runtime.ApplyWorld(world);
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
                runtime.RestoreDefaultCamera(camera);
                operation.DefaultCameraBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"default camera restore: {ex.Message}");
            }
        }

        // First out, because it is the one rollback step that GIVES SOMETHING
        // BACK rather than destroying it: whatever else fails below, the map
        // must not be left holding this load's displacements.
        Release(operation.BorrowedWorldObjects, runtime.ReleaseWorldObject,
            "world object release", failures);
        Release(operation.CreatedCameras, runtime.DestroyCamera,
            "camera", failures);
        Release(operation.SpawnedLights, runtime.DestroyLight,
            "light", failures);
        Release(operation.StagedOverlays, runtime.DestroyOverlay,
            "overlay", failures);
        Release(operation.SpawnedProps, runtime.DestroyProp,
            "object", failures);
        Release(operation.SpawnedActors, runtime.DestroyActor,
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
