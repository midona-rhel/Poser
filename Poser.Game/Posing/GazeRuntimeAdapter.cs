using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Gaze;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Posing;

public sealed class GazeRuntimeAdapter(
    IGazeService gaze, IEntityBindings bindings, SceneSession scene, IObjectTable objects)
    : IGazeRuntimePort
{
    public bool IsAvailable => gaze.IsAvailable;
    public string? UnavailableDetail => gaze.UnavailableDetail;

    public GazeReading? Read(ActorId id)
    {
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor }) return null;
        var state = gaze.GetGazeState(actor);
        ActorId? target = null;
        if (state.TargetId != 0 && !state.TargetStale)
            foreach (var candidate in scene.Snapshot.Actors)
                if (bindings.Resolve(candidate.Id) is { Success: true, Value: { } live } &&
                    objects.CreateObjectReference(live.Address) is { } native &&
                    native.GameObjectId == state.TargetId)
                {
                    target = candidate.Id;
                    break;
                }
        // Compare object identities, not SearchById's address: the overworld actor
        // and GPose clone share a game id, but have different native addresses.
        return new(new(state.Mode, state.TargetType, state.Position,
            state.EyesPosition, state.HeadPosition, state.BodyPosition,
            gaze.IsPartLocked(actor, GazeTargetType.Eyes),
            gaze.IsPartLocked(actor, GazeTargetType.Head),
            gaze.IsPartLocked(actor, GazeTargetType.Body)), target, state.Active, state.TargetStale);
    }

    private GazeResult WithActor(ActorId id, Func<IActor, GazeResult> change)
    {
        if (!IsAvailable) return GazeResult.Refused(UnavailableDetail ?? "Gaze unavailable.");
        return bindings.Resolve(id) is { Success: true, Value: { } actor }
            ? change(actor) : GazeResult.Refused("This actor is no longer available.");
    }

    private GazeResult Write(ActorId id, Action<IActor> change) =>
        WithActor(id, actor => { change(actor); return GazeResult.Ok(); });

    public GazeResult SetMode(ActorId actor, GazeTargetMode mode) =>
        WithActor(actor, live => gaze.SetGazeMode(live, mode));
    public GazeResult SetParts(ActorId actor, GazeTargetType parts) =>
        WithActor(actor, live => gaze.SetGazeParts(live, parts));
    public GazeResult SetTarget(ActorId actor, ActorId target) =>
        WithActor(actor, live => bindings.Resolve(target) is { Success: true, Value: { } other }
            ? gaze.SetGazeTarget(live, other) : GazeResult.Refused("The gaze target is no longer available."));
    public GazeResult SetPartLock(ActorId actor, GazeTargetType part, bool locked) =>
        Write(actor, live => gaze.SetPartLock(live, part, locked));
    public GazeResult SnapPartToCamera(ActorId actor, GazeTargetType part) =>
        Write(actor, live => gaze.SnapPartToCamera(live, part));
    public GazeResult Reset(ActorId actor) => Write(actor, gaze.ResetGaze);
    public GazeResult SetGazePosition(ActorId actor, Vector3 position) =>
        Write(actor, live => gaze.SetGazePosition(live, position));
    public GazeResult SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position) =>
        Write(actor, live => gaze.SetPartPosition(live, part, position));
}
