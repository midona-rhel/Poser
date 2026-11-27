using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Documents.Files;
using Poser.Application.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// Waits for actors, companion bodies, and pose imports with time limits.
/// SceneLoadTransaction decides whether a failure aborts the load or affects
/// only the named entity.
/// </summary>
internal sealed class SceneLoadBarriers(
    SceneWorkflow workflow, GPoseSceneState sceneState, SceneActorRestore actorRestore,
    SceneOperation operation,
    CancellationToken cancellation)
{
    /// <summary>How long to wait for attached companions' bodies to appear. It
    /// is short because a companion that doesn't appear only loses its pose,
    /// which the pose step names, rather than holding up the scene.</summary>
    private static readonly TimeSpan CompanionReadyTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Starts one pose import through <paramref name="arm"/> and waits for its
    /// final result. Returns null when it was applied, otherwise why not. The
    /// first, Pending result only gives the import's id, so it can be
    /// cancelled; its detail describes the request, not a failure.
    /// </summary>
    public async Task<string?> ImportPose(Func<Action<OperationReceipt>, string?> arm)
    {
        var completion = new TaskCompletionSource<OperationReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? admitted = null;
        void OnReceipt(OperationReceipt receipt)
        {
            if (receipt.State == OperationReceiptState.Pending)
                admitted = receipt.OperationId;
            else
                completion.TrySetResult(receipt);
        }

        // Other pose edits share the importer. Wait for it to become available
        // rather than failing this actor immediately while another edit finishes.
        (string? Refusal, bool Busy) TryArm()
        {
            if (operation.Guard(sceneState, cancellation) is { } stop)
                return (stop, false);
            if (actorRestore.PoseImportBusy)
                return (null, true);
            return (arm(OnReceipt), false);
        }
        (string? Refusal, bool Busy) armed = default;
        string? dispatchFailure = null;
        async Task<bool> Arm()
        {
            try
            {
                armed = await sceneState.OnFramework(TryArm);
            }
            catch (Exception ex)
            {
                dispatchFailure = $"The pose import dispatch failed: {ex.Message}";
                return true;
            }
            return !armed.Busy;
        }
        await FrameworkPoll.Until(Arm, SceneWorkflow.PoseImportTimeout, PollInterval, cancellation);
        if (dispatchFailure != null)
            return dispatchFailure;
        if (armed.Busy)
            return "Another pose edit held the pose import for the whole bound, so the pose was not restored.";
        if (armed.Refusal != null)
            return armed.Refusal;

        var finished = await Task.WhenAny(
            completion.Task, Task.Delay(SceneWorkflow.PoseImportTimeout, cancellation));
        if (finished != completion.Task)
        {
            // Cancel the pending import so it cannot change an actor after
            // this wait reports failure or the scene load rolls back.
            if (admitted is { } id)
            {
                try
                {
                    await sceneState.OnFramework(() =>
                    {
                        actorRestore.CancelPoseImport(id);
                        return true;
                    });
                }
                catch (Exception)
                {
                    // Cancellation could not be dispatched; preserve the timeout result.
                }
            }
            // Cancelling gives the import a Cancelled result. Only an import
            // that had already been applied counts.
            if (completion.Task is not { IsCompleted: true, Result.State: OperationReceiptState.Applied })
                return cancellation.IsCancellationRequested
                    ? "The load was cancelled."
                    : "The pose import did not finish within its bound, so it was cancelled.";
        }

        var receipt = completion.Task.Result;
        return receipt.State == OperationReceiptState.Applied
            ? null
            : receipt.Detail ?? $"The pose import ended {receipt.State}.";
    }

    /// <summary>Waits, up to <see cref="SceneWorkflow.ActorReadyTimeout"/>, until
    /// the actors can be posed. Stop is set when the whole load has to stop:
    /// cancelled, Poser shutting down, or the main thread gone. Unready lists
    /// the actors still not ready when time ran out; each is named in the
    /// result, and the rest of the load goes on.</summary>
    public async Task<(string? Stop, IReadOnlyList<SceneEntityHandle> Unready)> WaitForActors(
        IEnumerable<SceneEntityHandle> actors)
    {
        var pending = actors.ToList();
        string? failure = null;
        async Task<bool> Ready()
        {
            try
            {
                await sceneState.OnFramework(() =>
                {
                    if (!operation.Invalidated) // The guard below reports the refusal.
                        pending.RemoveAll(actorRestore.ActorReady);
                    return true;
                });
            }
            catch (Exception ex)
            {
                failure = $"The readiness barrier failed: {ex.Message}";
                return true;
            }
            return operation.Invalidated || cancellation.IsCancellationRequested
                || pending.Count == 0;
        }
        try
        {
            await FrameworkPoll.Until(Ready, SceneWorkflow.ActorReadyTimeout, PollInterval, workflow.DisposalToken);
        }
        catch (OperationCanceledException) when (workflow.DisposalToken.IsCancellationRequested)
        {
            return ("Poser is shutting down.", pending);
        }
        if (failure != null)
            return (failure, pending);
        if (operation.Invalidated || cancellation.IsCancellationRequested)
            return ("The load was cancelled.", pending);
        return (null, pending);
    }

    /// <summary>
    /// Waits for the bodies of the attached companions whose pose the scene
    /// holds. It returns when they have all appeared, when the GPose session
    /// changes, or when time runs out, and never fails the load: a companion
    /// that never appears is named by the pose step instead.
    /// </summary>
    public async Task WaitForCompanions(
        IReadOnlyList<SceneActor> actors, IReadOnlyDictionary<Guid, SceneEntityHandle> actorTokens)
    {
        async Task<bool> Built()
        {
            bool ready;
            try
            {
                ready = await sceneState.OnFramework(() =>
                {
                    if (operation.Invalidated)
                        return true;
                    foreach (var entry in actors)
                    {
                        if (entry.CompanionPose is null)
                            continue;
                        if (!actorRestore.CompanionReady(actorTokens[entry.Key]))
                            return false;
                    }
                    return true;
                });
            }
            catch (Exception)
            {
            // Stop waiting on dispatch failure; the pose phase reports readiness failures.
                return true;
            }
            return ready || operation.Invalidated || cancellation.IsCancellationRequested;
        }
        try
        {
            await FrameworkPoll.Until(Built, CompanionReadyTimeout, PollInterval, workflow.DisposalToken);
        }
        catch (OperationCanceledException) when (workflow.DisposalToken.IsCancellationRequested)
        {
            // Unload; the pose phase never runs.
        }
    }
}
