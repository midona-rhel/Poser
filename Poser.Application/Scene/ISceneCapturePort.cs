using Poser.Application.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>The save side of a scene: the armed whole-scene capture and the
/// appearance work between it and the write.</summary>
public interface ISceneCapturePort
{
    /// <summary>
    /// Schedules a scene capture after the bone caches refresh. Calls onCaptured
    /// with the result. Returns an error if capture could not be scheduled.
    /// Call on the framework thread.
    /// </summary>
    string? ArmSceneCapture(
        Guid sceneId, string? description, Action<SceneCaptureOutcome> onCaptured,
        bool includeAnimation = false);

    /// <summary>Hashes referenced character files and records the hashes in the scene.
    /// Returns notes for files that could not be read. Runs on the save worker.</summary>
    IReadOnlyList<string> StampMcdfHashes(SceneFile scene);

    /// <summary>Totals the file sizes of the actors' current appearance packages.</summary>
    long EstimateAppearanceBytes();

    /// <summary>
    /// Prepares appearance packages for the scene, reusing imported packages or
    /// exporting the actor's current appearance. Returns notes for actors whose
    /// appearance could not be included and temporary files to delete when the save finishes.
    /// Runs on the save worker and dispatches game reads to the framework thread.
    /// </summary>
    Task<SceneSealOutcome> SealAppearance(
        SceneFile scene,
        IReadOnlyDictionary<Guid, ActorId> identities,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Attempts to delete a temporary appearance package when the save finishes.</summary>
    void DeleteTemporary(string path);
}
