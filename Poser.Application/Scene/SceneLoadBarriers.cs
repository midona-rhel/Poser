using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>
/// The bounded waits ONE load runs between its phases: actor readiness,
/// companion bodies, and each atomic pose import's terminal receipt. None of
/// them decides what a miss costs — <see cref="SceneLoadTransaction"/>'s load
/// policy does.
/// </summary>
internal sealed class SceneLoadBarriers(
    SceneWorkflow workflow, ISceneRuntime runtime, SceneOperation operation,
    CancellationToken cancellation)
{
    /// <summary>Bound for every attached companion's own body to build. It is
    /// short because it is best-effort: the pose phase reports what did not
    /// make it, rather than the scene waiting on a companion that never
    /// draws.</summary>
    private static readonly TimeSpan CompanionReadyTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Arms ONE atomic pose import — an actor's or its companion's, through
    /// <paramref name="arm"/> — and awaits its TERMINAL receipt within a
    /// bound. Returns null on Applied, else the detail.
    ///
    /// <para>Pending receipts never complete the wait; they only name the
    /// operation to cancel if the wait ends first. The import
    /// engine acknowledges an admitted import by publishing a Pending receipt
    /// synchronously from inside <paramref name="arm"/>
    /// through the shared import coordinator,
    /// and that receipt's Detail is the import's DESCRIPTION. Completing on it
    /// made every scene pose import report itself failed with its own label —
    /// the reported "1 of 4 entities could not be restored" whose only stated
    /// reason was <c>Scene pose: &lt;actor&gt;</c>. Only a terminal state is an
    /// answer; <see cref="OperationReceiptState.Pending"/> is the explicit
    /// non-terminal acknowledgement and says nothing about the outcome.</para>
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

        // The slot is shared with every other pose feature, and an IK bake or
        // an open gesture holds it for a moment: WAIT for it, within the
        // bound, rather than spend this actor's one attempt on a busy answer.
        (string? Refusal, bool Busy) TryArm()
        {
            if (operation.Guard(runtime, cancellation) is { } stop)
                return (stop, false);
            if (runtime.PoseImportBusy)
                return (null, true);
            return (arm(OnReceipt), false);
        }
        (string? Refusal, bool Busy) armed = default;
        string? dispatchFailure = null;
        async Task<bool> Arm()
        {
            try
            {
                armed = await runtime.OnFramework(TryArm);
            }
            catch (Exception ex)
            {
                dispatchFailure = $"The pose import dispatch failed: {ex.Message}";
                return true;
            }
            return !armed.Busy;
        }
        await FrameworkPoll.Until(Arm, workflow.PoseImportBound, PollInterval, cancellation);
        if (dispatchFailure != null)
            return dispatchFailure;
        if (armed.Busy)
            return "Another pose edit held the pose import for the whole bound, so the pose was not restored.";
        if (armed.Refusal != null)
            return armed.Refusal;

        var finished = await Task.WhenAny(
            completion.Task, Task.Delay(workflow.PoseImportBound, cancellation));
        if (finished != completion.Task)
        {
            // Never leave the child armed: landing after this answer would
            // pose an actor behind the terminal receipt, or after rollback.
            if (admitted is { } id)
            {
                try
                {
                    await runtime.OnFramework(() =>
                    {
                        runtime.CancelPoseImport(id);
                        return true;
                    });
                }
                catch (Exception)
                {
                    // The framework thread is gone, and the import with it.
                }
            }
            // The cancel publishes the import's own Cancelled terminal; only
            // one that had already applied stands.
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

    /// <summary>Bounded readiness barrier over the given actors. Stop is a
    /// structural refusal (cancel, shutdown, framework gone); Unready names
    /// the actors still not posable when the bound ran out — each of them
    /// one named refusal, never a reason to roll the others back.</summary>
    public async Task<(string? Stop, IReadOnlyList<SceneEntityHandle> Unready)> WaitForActors(
        IEnumerable<SceneEntityHandle> actors)
    {
        var pending = actors.ToList();
        string? failure = null;
        async Task<bool> Ready()
        {
            try
            {
                await runtime.OnFramework(() =>
                {
                    if (!operation.Invalidated) // The guard below reports the refusal.
                        pending.RemoveAll(runtime.ActorReady);
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
            await FrameworkPoll.Until(Ready, workflow.ActorReadyBound, PollInterval, workflow.DisposalToken);
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
    /// Best-effort barrier over every attached companion whose pose the scene
    /// carries. It answers when they have all built, when the operation is
    /// invalidated, or when the bound expires — never as a failure, because a
    /// companion that never draws is the pose phase's named refusal to report,
    /// not a reason to tear down a restored scene.
    /// </summary>
    public async Task WaitForCompanions(
        IReadOnlyList<SceneActor> actors, IReadOnlyDictionary<Guid, SceneEntityHandle> actorTokens)
    {
        async Task<bool> Built()
        {
            bool ready;
            try
            {
                ready = await runtime.OnFramework(() =>
                {
                    if (operation.Invalidated)
                        return true;
                    foreach (var entry in actors)
                    {
                        if (entry.CompanionPose is null)
                            continue;
                        if (!runtime.CompanionReady(actorTokens[entry.Key]))
                            return false;
                    }
                    return true;
                });
            }
            catch (Exception)
            {
                // The framework thread is gone; the pose phase reports it.
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
