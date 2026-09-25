using Poser.Application.Appearance;
using Poser.Application.Gaze;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

public sealed record ActorPropertiesSnapshot(int ModelId, ActorAppearanceSnapshot Appearance,
    PresentationOverrides Presentation, GazeReading? Gaze,
    IReadOnlyList<(string Id, float Weight)>? Expression);

public sealed record ActorStateSnapshot(ActorId Actor, SessionGeneration Session,
    ActorSnapshot Pose, ActorPropertiesSnapshot Properties);

public interface IActorStateSnapshots
{
    IntegrationValue<ActorStateSnapshot> Capture(ActorId actor);
    void Restore(ActorStateSnapshot snapshot, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed);
    void WaitForReset(ActorId actor, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed);
}

/// <summary>Coordinates the existing state owners; no animation playback is captured or restored.</summary>
public sealed class ActorStateSnapshots(
    SceneSession scene, ISessionGenerationSource sessions, Lazy<IPoseSnapshotPort> poses,
    ActorIntegrationSession appearance, ActorPresentationSession presentation,
    ActorModelIdSession models, GazeSession gaze, IExpressionRuntimePort expressions,
    IIntegrationRuntimePort runtime) : IActorStateSnapshots
{
    public IntegrationValue<ActorPropertiesSnapshot> CaptureProperties(ActorId actor, bool captureCollection = true)
    {
        try
        {
            if (models.Read(actor) is not { } model)
                return IntegrationValue<ActorPropertiesSnapshot>.Fail("The actor's model could not be captured.");
            var look = appearance.TryCaptureHistory(actor, captureCollection);
            if (!look.Success || look.Value == null)
                return IntegrationValue<ActorPropertiesSnapshot>.Fail(look.Detail ?? "Appearance capture failed.");
            var gazeState = gaze.IsAvailable ? gaze.Read(actor) : null;
            if (gaze.IsAvailable && gazeState == null)
                return IntegrationValue<ActorPropertiesSnapshot>.Fail("The actor's gaze could not be captured.");
            if (expressions.IsAvailable && !expressions.HasActor(actor))
                return IntegrationValue<ActorPropertiesSnapshot>.Fail("The actor's expression could not be captured.");
            var weights = expressions.IsAvailable ? expressions.GetUnits(actor)
                .Where(u => u.Available).Select(u => (u.Id, expressions.GetWeight(actor, u.Id))).ToArray() : null;
            return IntegrationValue<ActorPropertiesSnapshot>.Ok(new(model, look.Value,
                presentation.OverridesFor(actor), gazeState, weights));
        }
        catch (Exception ex) { return IntegrationValue<ActorPropertiesSnapshot>.Fail(ex.Message); }
    }

    public IntegrationValue<ActorStateSnapshot> Capture(ActorId actor)
    {
        if (sessions.ActiveSessionGeneration is not { IsValid: true } session || !Current(actor, session))
            return IntegrationValue<ActorStateSnapshot>.Fail("The actor or GPose session is no longer available.");
        var properties = CaptureProperties(actor);
        if (!properties.Success || properties.Value == null)
            return IntegrationValue<ActorStateSnapshot>.Fail(properties.Detail ?? "Actor state could not be captured.");
        try
        {
            if (poses.Value.CaptureAuthored(actor.LogicalId) is not { } pose)
                return IntegrationValue<ActorStateSnapshot>.Fail("The actor's pose and IK could not be captured.");
            return IntegrationValue<ActorStateSnapshot>.Ok(new(actor, session, pose, properties.Value));
        }
        catch (Exception ex) { return IntegrationValue<ActorStateSnapshot>.Fail(ex.Message); }
    }

    private bool Current(ActorId actor, SessionGeneration session) =>
        sessions.ActiveSessionGeneration == session && scene.Contains(TransformTargetId.ForActor(actor));

    public void Restore(ActorStateSnapshot snapshot, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed) => _ = Complete(async () =>
    {
        bool CurrentRestore() => !cancellation.IsCancellationRequested && stillCurrent()
            && Current(snapshot.Actor, snapshot.Session);
        var appearanceResult = await RestoreAppearance(snapshot.Actor, snapshot.Properties, CurrentRestore, cancellation);
        if (!appearanceResult.Success) return GestureResult.Fail(appearanceResult.Detail!);

        var poseDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await runtime.OnFrameworkThread(() => CurrentRestore()
            && poses.Value.Restore(snapshot.Pose, CurrentRestore, ok => poseDone.TrySetResult(ok)));
        if (!started || !await poseDone.Task.WaitAsync(cancellation))
            return GestureResult.Fail("The actor's pose or IK could not be restored. The history entry remains available to retry.");
        return await runtime.OnFrameworkThread(() => CurrentRestore()
            ? RestoreProperties(snapshot.Actor, snapshot.Properties)
            : GestureResult.Fail("The actor restoration is no longer current."));
    }, completed);

    /// <summary>Appearance/model first. Call pose and presentation restoration only after this completes.</summary>
    public async Task<IntegrationResult> RestoreAppearance(ActorId actor, ActorPropertiesSnapshot properties,
        Func<bool> stillCurrent, CancellationToken cancellation)
    {
        var look = await appearance.RestoreHistoryAndWait(actor, properties.Appearance, stillCurrent, cancellation);
        if (!look.Success) return look;
        var model = await runtime.OnFrameworkThread(() =>
        {
            if (cancellation.IsCancellationRequested || !stillCurrent())
                return PresentationResult.Fail("The actor restoration is no longer current.");
            return models.Read(actor) == properties.ModelId
                ? PresentationResult.Ok() : models.Apply(actor, properties.ModelId);
        });
        if (!model.Success) return IntegrationResult.Fail(model.Detail!);
        // MCDF already uses the same barrier, but a restored model id can require
        // a subsequent redraw. Ordinary looks and collections also finish here.
        if (await runtime.OnFrameworkThread(() => runtime.Penumbra.Available))
        {
            var ready = await runtime.RedrawAndWait(actor, TimeSpan.FromSeconds(10), cancellation);
            if (!ready.Success) return IntegrationResult.Fail(ready.Detail!);
        }
        return IntegrationResult.Ok();
    }

    /// <summary>Shared post-readiness restoration. The pose importer remains the sole pose/IK owner.</summary>
    public GestureResult RestoreProperties(ActorId actor, ActorPropertiesSnapshot properties)
    {
        var present = presentation.RestoreOverrides(actor, properties.Presentation);
        if (!present.Success) return GestureResult.Fail(present.Detail!);
        if (properties.Gaze is { } savedGaze)
        {
            var result = gaze.RestoreState(actor, savedGaze);
            if (!result.Success) return GestureResult.Fail(result.Detail ?? "Gaze restoration failed.");
        }
        if (properties.Expression is { } weights)
        {
            var result = expressions.Write(actor, weights, reset: true);
            if (!result.Success) return GestureResult.Fail(result.Detail ?? "Expression restoration failed.");
        }
        return GestureResult.Ok();
    }

    public void WaitForReset(ActorId actor, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed)
    {
        var session = sessions.ActiveSessionGeneration;
        _ = Complete(async () =>
        {
            await appearance.PendingCompletion.WaitAsync(cancellation);
            var check = await runtime.OnFrameworkThread(() =>
            {
                if (cancellation.IsCancellationRequested || !stillCurrent()
                    || session == null || !Current(actor, session.Value))
                    return GestureResult.Fail("The actor reset is no longer current.");
                return appearance.OverridesFor(actor).Mcdf is { RedrawPending: true }
                    ? GestureResult.Fail("The reset redraw did not complete. Retry after the actor is ready.")
                    : GestureResult.Ok();
            });
            if (!check.Success) return check;
            if (await runtime.OnFrameworkThread(() => runtime.Penumbra.Available))
            {
                var ready = await runtime.RedrawAndWait(actor, TimeSpan.FromSeconds(10), cancellation);
                if (!ready.Success) return GestureResult.Fail(ready.Detail!);
            }
            return await runtime.OnFrameworkThread(() => !cancellation.IsCancellationRequested && stillCurrent()
                && session != null && Current(actor, session.Value)
                ? GestureResult.Ok() : GestureResult.Fail("The actor reset is no longer current."));
        }, completed);
    }

    private async Task Complete(Func<Task<GestureResult>> run, Action<GestureResult> completed)
    {
        GestureResult result;
        try { result = await run(); }
        catch (OperationCanceledException) { result = GestureResult.Fail("Actor restoration was cancelled."); }
        catch (Exception ex) { result = GestureResult.Fail($"Actor restoration failed: {ex.Message}"); }
        // History and state ownership are application-thread confined.
        await runtime.OnFrameworkThread(() => { completed(result); return true; });
    }
}
