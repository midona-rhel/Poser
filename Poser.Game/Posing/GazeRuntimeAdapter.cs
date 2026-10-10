using System.Numerics;
using Poser.Application.Gaze;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

public sealed class GazeRuntimeAdapter(
    GazeService gaze, IEntityBindings bindings)
    : IGazeRuntimePort
{
    public bool IsAvailable => gaze.IsAvailable;
    public string? UnavailableDetail => gaze.UnavailableDetail;

    public GazeReading? Read(ActorId id)
    {
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor }) return null;
        var state = gaze.GetGazeState(actor);
        var target = state.TargetStale ? null : state.TargetActor;
        return new(new(state.Mode, state.TargetType, state.Position,
            state.EyesPosition, state.HeadPosition, state.BodyPosition,
            gaze.IsPartLocked(actor, GazeTargetType.Eyes),
            gaze.IsPartLocked(actor, GazeTargetType.Head),
            gaze.IsPartLocked(actor, GazeTargetType.Body)) { PoseAware = state.PoseAware }, target, state.Active, state.TargetStale);
    }

    private Outcome WithActor(ActorId id, Func<IActor, Outcome> change)
    {
        if (!IsAvailable) return Outcome.Fail(UnavailableDetail ?? "Gaze unavailable.");
        return bindings.Resolve(id) is { Success: true, Value: { } actor }
            ? change(actor) : Outcome.Fail("This actor is no longer available.");
    }

    private Outcome Write(ActorId id, Action<IActor> change) =>
        WithActor(id, actor => { change(actor); return Outcome.Ok(); });

    public Outcome RestoreSettings(ActorId actor, GazeSettings settings) =>
        WithActor(actor, live => gaze.RestoreSettings(live, settings));

    public Outcome SetMode(ActorId actor, GazeTargetMode mode) =>
        WithActor(actor, live => gaze.SetGazeMode(live, mode));
    public Outcome SetParts(ActorId actor, GazeTargetType parts) =>
        WithActor(actor, live => gaze.SetGazeParts(live, parts));
    public Outcome SetTarget(ActorId actor, ActorId target) =>
        WithActor(actor, live => bindings.Resolve(target) is { Success: true, Value: { } other }
            ? gaze.SetGazeTarget(live, other) : Outcome.Fail("The gaze target is no longer available."));
    public Outcome SetPartLock(ActorId actor, GazeTargetType part, bool locked) =>
        Write(actor, live => gaze.SetPartLock(live, part, locked));
    public Outcome SnapPartToCamera(ActorId actor, GazeTargetType part) =>
        Write(actor, live => gaze.SnapPartToCamera(live, part));
    public Outcome Reset(ActorId actor) => Write(actor, gaze.ResetGaze);
    public Outcome SetGazePosition(ActorId actor, Vector3 position) =>
        Write(actor, live => gaze.SetGazePosition(live, position));
    public Outcome SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position) =>
        Write(actor, live => gaze.SetPartPosition(live, part, position));
}
