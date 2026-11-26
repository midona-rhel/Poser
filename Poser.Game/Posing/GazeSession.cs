using System.Numerics;
using Poser.Application.Gaze;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

/// <summary>
/// The gaze commands behind the Gaze controls in the inspector and the gaze
/// point on the gizmo. Each command finds the actor again by its id, so a
/// command meant for a removed actor never changes the actor that took its
/// place. Each change is one undo step, and dragging a gaze point is one step
/// for the whole drag. Undo puts back the gaze settings and points, not the
/// actor being looked at.
/// </summary>
public sealed class GazeSession(GazeService gaze, IEntityBindings bindings, ValueJournal journal) : IGazeControl
{
    public bool IsAvailable => gaze.IsAvailable;
    public string? UnavailableDetail => gaze.UnavailableDetail;
    public void Seal() => journal.Seal();

    public GazeReading? Read(ActorId id)
    {
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor }) return null;
        var state = gaze.GetGazeState(actor);
        var target = state.TargetStale ? null : state.TargetActor;
        return new(new(state.Mode, state.TargetType, state.Position,
            state.EyesPosition, state.HeadPosition, state.BodyPosition,
            gaze.IsPartLocked(actor, GazeTargetType.Eyes),
            gaze.IsPartLocked(actor, GazeTargetType.Head),
            gaze.IsPartLocked(actor, GazeTargetType.Body)), target, state.Active, state.TargetStale);
    }

    public Outcome SetMode(ActorId actor, GazeTargetMode mode) =>
        Step(actor, "Set gaze mode", () => Change(actor, live => gaze.SetGazeMode(live, mode)));
    public Outcome SetParts(ActorId actor, GazeTargetType parts) =>
        Step(actor, "Set gaze parts", () => Change(actor, live => gaze.SetGazeParts(live, parts)));
    public Outcome SetTarget(ActorId actor, ActorId target) =>
        Step(actor, "Set gaze target", () => LookAt(actor, target));
    public Outcome SetPartLock(ActorId actor, GazeTargetType part, bool locked) =>
        Step(actor, locked ? "Lock gaze part" : "Unlock gaze part",
            () => Write(actor, live => gaze.SetPartLock(live, part, locked)));
    public Outcome SnapPartToCamera(ActorId actor, GazeTargetType part) =>
        Step(actor, "Snap gaze to camera", () => Write(actor, live => gaze.SnapPartToCamera(live, part)));
    public Outcome Reset(ActorId actor) =>
        Step(actor, "Reset gaze", () => ResetWithoutUndoStep(actor));

    public Outcome SetGazePosition(ActorId actor, Vector3 position) =>
        Adjust(actor, GazeTargetType.None, position);
    public Outcome SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position) =>
        Adjust(actor, part, position);

    /// <summary>Puts back a whole saved gaze, including the actor being looked
    /// at, without adding an undo step. Undo of an actor's whole state uses this.</summary>
    public Outcome RestoreState(ActorId actor, GazeReading reading)
    {
        if (reading.Target is { } target)
        {
            var targetResult = LookAt(actor, target);
            if (!targetResult.Success) return targetResult;
        }
        return RestoreSettings(actor, reading.Settings);
    }

    /// <summary>Resets the gaze without adding an undo step. Reset → All uses
    /// this because it records the whole actor reset as one step of its own.</summary>
    public Outcome ResetWithoutUndoStep(ActorId actor) => Write(actor, gaze.ResetGaze);

    private Outcome RestoreSettings(ActorId actor, GazeSettings settings) =>
        Change(actor, live => gaze.RestoreSettings(live, settings));

    private Outcome LookAt(ActorId actor, ActorId target) =>
        Change(actor, live => bindings.Resolve(target) is { Success: true, Value: { } other }
            ? gaze.SetGazeTarget(live, other) : Outcome.Fail("The gaze target is no longer available."));

    private static Outcome Missing() => Outcome.Fail("This actor is no longer available.");

    private Outcome Change(ActorId id, Func<IActor, Outcome> change)
    {
        if (!IsAvailable) return Outcome.Fail(UnavailableDetail ?? "Gaze unavailable.");
        return bindings.Resolve(id) is { Success: true, Value: { } actor } ? change(actor) : Missing();
    }

    private Outcome Write(ActorId id, Action<IActor> change) =>
        Change(id, actor => { change(actor); return Outcome.Ok(); });

    private Outcome Step(ActorId actor, string description, Func<Outcome> change)
    {
        if (Read(actor) is not { } before) return Missing();
        var result = change();
        if (result.Success && Read(actor) is { } after)
            journal.Record(SelectionId.ForActor(actor), description, before.Settings, after.Settings,
                settings => RestoreSettings(actor, settings), () => Read(actor) is not null);
        return result;
    }

    private Outcome Adjust(ActorId actor, GazeTargetType part, Vector3 position)
    {
        if (Read(actor) is not { Settings.Mode: GazeTargetMode.Position })
            return Outcome.Fail("A live Point-mode gaze target is required.");
        return journal.Adjust((actor, part), "Move gaze point",
            () => Read(actor)!.Settings.PartPosition(part),
            next => part == GazeTargetType.None
                ? Write(actor, live => gaze.SetGazePosition(live, next))
                : Write(actor, live => gaze.SetPartPosition(live, part, next)),
            position, () => Read(actor) is not null);
    }
}
