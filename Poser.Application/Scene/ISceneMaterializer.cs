using Poser.Application.Scene;
using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>
/// Creates the entities a load restores — actors, props, overlays, map
/// objects, lights and cameras — and is the rollback's exact inverse for each.
/// Framework thread unless a member says otherwise.
/// </summary>
public interface ISceneMaterializer
{
    /// <summary>Resolve the catalog label without exposing the native catalog to workflow policy.</summary>
    string WorldObjectName(string path);

    /// <summary>Spawns one actor and applies its model id. Null with the
    /// spawn's own refusal on failure.</summary>
    SceneEntityHandle? SpawnActor(SceneActor data, out string? detail);

    /// <summary>Spawns one prop with its transform and visibility.</summary>
    SceneEntityHandle? SpawnProp(SceneProp data, out string? detail);

    /// <summary>Stages one overlay node from its saved document.</summary>
    SceneEntityHandle? SpawnOverlay(SceneOverlay data, out string? detail);

    /// <summary>
    /// Restores one world-object entry by spawning its path with the
    /// placement and state the file recorded. Null with a stated detail when
    /// it cannot be spawned.
    /// </summary>
    SceneEntityHandle? AdoptWorldObject(SceneWorldObject data, out string? detail);

    /// <summary>Waits, within <paramref name="bound"/>, for spawned world
    /// objects whose models are still streaming (housing furniture). Answers
    /// the ones still not loaded at the bound: they are KEPT and no longer
    /// released on a timer, so the load can name them instead of reporting a
    /// restore that later disappears. Runs from the workflow task.</summary>
    Task<IReadOnlyList<SceneEntityHandle>> AwaitWorldObjectsLoaded(
        IReadOnlyList<SceneEntityHandle> worldObjects, TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Gives one restored map object back — the rollback verb, and the
    /// exact inverse of <see cref="AdoptWorldObject"/>. It RESTORES rather than
    /// destroys, which is why it is not named with the others.</summary>
    void ReleaseWorldObject(SceneEntityHandle token);

    /// <summary>Spawns one light with its complete document, gobo, and — when
    /// an attachment is stated — the exact resolved bone on the restored
    /// owner. An unresolvable attachment returns null with a detail and
    /// spawns NOTHING: a light is never silently detached into world space.
    /// </summary>
    SceneEntityHandle? SpawnLight(SceneLight data, SceneEntityHandle? attachmentOwner, out string? detail);

    /// <summary>Snapshot of the session default camera for rollback: its
    /// document, its followed actor and which camera is live.</summary>
    SceneCameraBaseline CaptureDefaultCameraState();

    /// <summary>Applies a scene camera document onto the session default
    /// camera; null on success.</summary>
    string? ApplyDefaultCamera(SceneCamera data);

    /// <summary>The session default camera as a structure token, so a
    /// saved group that held the Main Camera re-seats it on load.</summary>
    SceneEntityHandle? DefaultCameraToken();

    /// <summary>Creates one additional camera from its document.</summary>
    SceneEntityHandle? CreateCamera(SceneCamera data, out string? detail);

    /// <summary>Sets a camera's followed actor and saved identity-lock state
    /// (null camera = the default camera); null on success.</summary>
    string? SetCameraTarget(
        SceneEntityHandle? camera, SceneEntityHandle targetActor, string displayName,
        bool targetLocked);

    /// <summary>Makes a camera live (null = the default camera).</summary>
    string? SetLiveCamera(SceneEntityHandle? camera);

    void RestoreDefaultCamera(SceneCameraBaseline baseline);

    void DestroyActor(SceneEntityHandle actor);
    void DestroyProp(SceneEntityHandle prop);
    void DestroyOverlay(SceneEntityHandle overlay);
    void DestroyLight(SceneEntityHandle light);
    void DestroyCamera(SceneEntityHandle camera);
}

/// <summary>What a load can change about the session's cameras besides the
/// cameras it creates: the default camera's document, its followed actor
/// (null when it followed none) and the live camera. Rollback restores all
/// three, so the default camera never keeps following an actor the rollback
/// removed.</summary>
public sealed record SceneCameraBaseline(
    CameraFile Camera,
    SceneEntityHandle? Target,
    string TargetName,
    bool TargetLocked,
    SceneEntityHandle? Live);
