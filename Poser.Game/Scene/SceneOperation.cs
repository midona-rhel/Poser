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
    /// <summary>The user asked to cancel; later cancellable steps keep
    /// reading "Cancelling" until the terminal state lands.</summary>
    public bool CancelRequested;
    /// <summary>Whether a landed load appends its step. A load the
    /// journal itself started as a redo does not: its step is the one
    /// being redone.</summary>
    public SceneLoadReplay? Replay;
    /// <summary>The destroy-first clear ran: a rollback cannot give the
    /// session back what the clear took, and the outcome says so.</summary>
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
    /// <summary>Children whose parent link this load imported; rollback
    /// removes them before the entities go.</summary>
    public readonly List<SelectionId> ImportedLinks = new();
    /// <summary>The load reached its commit (Applied, or Failed with
    /// named refusals): it is in the session and is one history step.
    /// </summary>
    public bool Committed;
    public string? TerminalDetail;
    public SceneEnvironment? EnvironmentBaseline;
    public SceneWorld? WorldBaseline;

    /// <summary>Checked at the top of every framework-thread action before
    /// its mutations. A replaced session generation is an invalidation: the
    /// token that admitted this operation no longer exists.</summary>
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
