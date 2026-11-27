using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using Poser.Application.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// One running scene save or load: the ids its result is filed under, whether
/// it was cancelled, and, for a load, the list of everything it created.
/// <see cref="SceneLoadRollback"/> removes those again, newest first.
/// </summary>
internal sealed class SceneOperation : SingleFlightOperation
{
    public required Guid SceneScopeId { get; init; }
    public required string FileName { get; init; }
    public required SceneOperationKind Kind { get; init; }
    /// <summary>Cancel was pressed. From then on every step that can be
    /// cancelled shows "Cancelling" until the final result is in.</summary>
    public bool CancelRequested;
    /// <summary>Set when this load is a redo. A finished load adds an undo
    /// step, except a redo: its step is the one being redone.</summary>
    public SceneLoadReplay? Replay;
    /// <summary>The scene was cleared before the load. Taking the load back
    /// cannot bring back what the clear removed, and the result says so.</summary>
    public bool SessionCleared;

    // What this load created, oldest first. Undoing the load removes them
    // newest first. They are handles, never game memory.
    public readonly List<SceneEntityHandle> SpawnedActors = new();
    public readonly List<Action> ActorRestores = new();
    public readonly List<SceneEntityHandle> SpawnedProps = new();
    public readonly List<SceneEntityHandle> StagedOverlays = new();
    public readonly List<SceneEntityHandle> SpawnedLights = new();
    public readonly List<SceneEntityHandle> CreatedCameras = new();

    // Map objects the load borrowed or spawned. Undoing the load gives each
    // one back through SceneEntityLoader.ReleaseWorldObject.
    public readonly List<SceneEntityHandle> BorrowedWorldObjects = new();
    public readonly List<Guid> ImportedGroups = new();
    public IReadOnlyDictionary<Guid, Guid> HistoryGroups = new Dictionary<Guid, Guid>();
    public IReadOnlyDictionary<(string Kind, Guid Key), SceneEntityHandle> HistoryEntities =
        new Dictionary<(string Kind, Guid Key), SceneEntityHandle>();
    public SceneCameraBaseline? DefaultCameraBaseline;
    /// <summary>Entities this load gave a parent. Taking the load back removes
    /// those links before it removes the entities.</summary>
    public readonly List<SelectionId> ImportedLinks = new();
    /// <summary>The load finished and was kept, either complete or with some
    /// parts named as failed. It is in the scene and is one undo step.
    /// </summary>
    public bool Committed;
    public string? TerminalDetail;
    public SceneEnvironment? EnvironmentBaseline;
    public SceneWorld? WorldBaseline;

    /// <summary>Checked at the start of every step on the main thread, before
    /// it changes anything. Returns why the operation must stop: it was
    /// cancelled, or the GPose session it started in has ended.</summary>
    public string? Guard(GPoseSceneState sceneState, CancellationToken cancellation)
    {
        if (Invalidated || cancellation.IsCancellationRequested)
            return Kind == SceneOperationKind.Save
                ? "The save was cancelled."
                : "The load was cancelled.";
        if (sceneState.ActiveSession is not { } live || live != Session)
        {
            Invalidated = true;
            return "The GPose session ended before the operation completed.";
        }
        return null;
    }
}
