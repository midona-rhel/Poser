using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Gaze;

/// <summary>Gaze edit/history policy, independent of native actors and panel lifetime.</summary>
public sealed class GazeSession(ValueJournal journal, IGazeRuntimePort runtime) : IGazeControl
{
    public bool IsAvailable => runtime.IsAvailable;
    public string? UnavailableDetail => runtime.UnavailableDetail;
    public GazeReading? Read(ActorId actor) => runtime.Read(actor);
    public void Seal() => journal.Seal();

    private static Outcome Missing() => Outcome.Fail("This actor is no longer available.");

    // Preserve existing history scope: settings/points, not native entity retargeting.
    private Outcome Restore(ActorId actor, GazeSettings settings) =>
        runtime.RestoreSettings(actor, settings);

    /// <summary>Shared full-state restore, without creating another history entry.</summary>
    public Outcome RestoreState(ActorId actor, GazeReading reading)
    {
        if (reading.Target is { } target)
        {
            var targetResult = runtime.SetTarget(actor, target);
            if (!targetResult.Success) return targetResult;
        }
        return runtime.RestoreSettings(actor, reading.Settings);
    }

    private Outcome Step(ActorId actor, string description, Func<Outcome> change)
    {
        if (Read(actor) is not { } before) return Missing();
        var result = change();
        if (result.Success && Read(actor) is { } after)
            journal.Record(SelectionId.ForActor(actor), description, before.Settings, after.Settings,
                settings => Restore(actor, settings), () => Read(actor) is not null);
        return result;
    }

    public Outcome SetMode(ActorId actor, GazeTargetMode mode) =>
        Step(actor, "Set gaze mode", () => runtime.SetMode(actor, mode));
    public Outcome SetPoseAware(ActorId actor, bool enabled) =>
        Step(actor, "Set pose-aware gaze", () => Read(actor) is { } state
            ? runtime.RestoreSettings(actor, state.Settings with { PoseAware = enabled }) : Missing());
    public Outcome SetParts(ActorId actor, GazeTargetType parts) =>
        Step(actor, "Set gaze parts", () => runtime.SetParts(actor, parts));
    public Outcome SetTarget(ActorId actor, ActorId target) =>
        Step(actor, "Set gaze target", () => runtime.SetTarget(actor, target));
    public Outcome SetPartLock(ActorId actor, GazeTargetType part, bool locked) =>
        Step(actor, locked ? "Lock gaze part" : "Unlock gaze part",
            () => runtime.SetPartLock(actor, part, locked));
    public Outcome SnapPartToCamera(ActorId actor, GazeTargetType part) =>
        Step(actor, "Snap gaze to camera", () => runtime.SnapPartToCamera(actor, part));
    public Outcome Reset(ActorId actor) =>
        Step(actor, "Reset gaze", () => runtime.Reset(actor));

    public Outcome SetGazePosition(ActorId actor, Vector3 position) =>
        Adjust(actor, GazeTargetType.None, position);
    public Outcome SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position) =>
        Adjust(actor, part, position);

    private Outcome Adjust(ActorId actor, GazeTargetType part, Vector3 position)
    {
        if (Read(actor) is not { Settings.Mode: GazeTargetMode.Position })
            return Outcome.Fail("A live Point-mode gaze target is required.");
        return journal.Adjust((actor, part), "Move gaze point",
            () => Read(actor)!.Settings.PartPosition(part),
            next => part == GazeTargetType.None
                ? runtime.SetGazePosition(actor, next)
                : runtime.SetPartPosition(actor, part, next),
            position, () => Read(actor) is not null);
    }
}
