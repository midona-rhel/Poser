using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;
using Poser.Domain.Transforms;
using Poser.Game.Appearance;

namespace Poser.Game.Posing;

/// <summary>
/// Saves and puts back an actor's whole state except animation, by asking the
/// classes that own each part: the pose and IK, the Penumbra, Glamourer and
/// Customize+ look, the model id, opacity and colours, gaze and expression.
/// Putting it back runs in that order: first the look and model id, which can
/// redraw the actor, then the pose once the redraw is done, then the rest.
/// Undo of removing an actor uses the same steps to put its look back.
/// </summary>
public sealed class ActorStateSnapshots(
    SceneSession scene, ISessionGenerationSource sessions, Lazy<IPoseSnapshotPort> poses,
    IntegrationSelectors appearance, McdfTransaction mcdf, ActorPresentationSession presentation,
    ActorModelIdSession models, GazeSession gaze, IExpressionRuntimePort expressions,
    IIntegrationResolutionPort runtime, IPenumbraPort penumbra) : IActorStateSnapshots
{
    // The last pose and IK saved for each actor in this GPose session. While
    // the actor is being redrawn its pose can't be read, so Capture uses this.
    private readonly Dictionary<ActorId, (SessionGeneration Session, ActorSnapshot Pose)> _lastPose = new();

    public IntegrationValue<ActorPropertiesSnapshot> CaptureProperties(ActorId actor, bool captureCollection = true,
        bool omitUnreadableLook = false)
    {
        try
        {
            if (models.Read(actor) is not { } model)
                return IntegrationValue<ActorPropertiesSnapshot>.Fail("The actor's model could not be captured.");
            var look = appearance.TryCaptureHistory(actor, captureCollection, omitUnreadableLook);
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
            var pose = poses.Value.CaptureAuthored(actor.LogicalId);
            if (pose != null)
                _lastPose[actor] = (session, pose);
            else if (_lastPose.TryGetValue(actor, out var last) && last.Session == session)
                pose = last.Pose;
            else
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
        if (!appearanceResult.Success) return GestureResult.From(appearanceResult.Outcome);

        var poseDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await runtime.OnFrameworkThread(() => CurrentRestore()
            && poses.Value.Restore(snapshot.Pose, CurrentRestore, ok => poseDone.TrySetResult(ok)));
        if (!started || !await poseDone.Task.WaitAsync(cancellation))
            return GestureResult.Fail("The actor's pose or IK could not be restored. The history entry remains available to retry.");
        return await runtime.OnFrameworkThread(() => CurrentRestore()
            ? RestoreProperties(snapshot.Actor, snapshot.Properties)
            : GestureResult.Fail("The actor restoration is no longer current."));
    }, completed);

    /// <summary>Puts back the look and the model id, then redraws the actor and
    /// waits until it is drawn again. Restore the pose and the rest only after this.</summary>
    public async Task<IntegrationResult> RestoreAppearance(ActorId actor, ActorPropertiesSnapshot properties,
        Func<bool> stillCurrent, CancellationToken cancellation)
    {
        var look = await appearance.RestoreHistoryAndWait(actor, properties.Appearance, stillCurrent, cancellation);
        if (!look.Success) return look;
        var model = await runtime.OnFrameworkThread(() =>
        {
            if (cancellation.IsCancellationRequested || !stillCurrent())
                return Outcome.Fail("The actor restoration is no longer current.");
            return models.Read(actor) == properties.ModelId
                ? Outcome.Ok() : models.Apply(actor, properties.ModelId);
        });
        if (!model.Success) return IntegrationResult.From(model);
        // A changed collection, design or model id only shows after a redraw.
        return await RedrawIfPenumbra(actor, cancellation);
    }

    /// <summary>Redraws through Penumbra and waits for the actor to be drawn
    /// again. Without Penumbra there is nothing to redraw for.</summary>
    private async Task<IntegrationResult> RedrawIfPenumbra(ActorId actor, CancellationToken cancellation) =>
        await runtime.OnFrameworkThread(() => penumbra.Penumbra.Available)
            ? await penumbra.RedrawAndWait(actor, TimeSpan.FromSeconds(10), cancellation)
            : IntegrationResult.Ok();

    /// <summary>Puts back opacity and colours, gaze and expression. Call it after
    /// the actor is drawn again; the pose is put back by the pose import, not here.</summary>
    public GestureResult RestoreProperties(ActorId actor, ActorPropertiesSnapshot properties)
    {
        var present = presentation.RestoreOverrides(actor, properties.Presentation);
        if (!present.Success) return GestureResult.From(present);
        if (properties.Gaze is { } savedGaze)
        {
            var result = gaze.RestoreState(actor, savedGaze);
            if (!result.Success) return GestureResult.From(result);
        }
        if (properties.Expression is { } weights)
        {
            var result = expressions.Write(actor, weights, reset: true);
            if (!result.Success) return GestureResult.From(result);
        }
        return GestureResult.Ok();
    }

    public void WaitForReset(ActorId actor, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed)
    {
        var session = sessions.ActiveSessionGeneration;
        _ = Complete(async () =>
        {
            await mcdf.CurrentCompletion.WaitAsync(cancellation);
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
            var ready = await RedrawIfPenumbra(actor, cancellation);
            if (!ready.Success) return GestureResult.From(ready.Outcome);
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
        // Undo history and the saved states are only touched on the game's framework thread.
        await runtime.OnFrameworkThread(() => { completed(result); return true; });
    }
}
