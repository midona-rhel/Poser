using System.Numerics;
using Poser.Application.Transforms;
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

    private static GazeResult Missing() => GazeResult.Refused("This actor is no longer available.");
    private static ValueWriteResult Written(GazeResult result) => new(result.Success, result.Detail);

    // Preserve existing history scope: settings/points, not native entity retargeting.
    private ValueWriteResult Restore(ActorId actor, GazeSettings settings) =>
        Written(runtime.RestoreSettings(actor, settings));

    /// <summary>Shared full-state restore, without creating another history entry.</summary>
    public GazeResult RestoreState(ActorId actor, GazeReading reading)
    {
        if (reading.Target is { } target)
        {
            var targetResult = runtime.SetTarget(actor, target);
            if (!targetResult.Success) return targetResult;
        }
        return runtime.RestoreSettings(actor, reading.Settings);
    }

    private GazeResult Step(ActorId actor, string description, Func<GazeResult> change)
    {
        if (Read(actor) is not { } before) return Missing();
        var result = change();
        if (result.Success && Read(actor) is { } after)
            journal.RecordResult(description, before.Settings, after.Settings,
                settings => Restore(actor, settings), () => Read(actor) is not null);
        return result;
    }

    public GazeResult SetMode(ActorId actor, GazeTargetMode mode) =>
        Step(actor, "Set gaze mode", () => runtime.SetMode(actor, mode));
    public GazeResult SetParts(ActorId actor, GazeTargetType parts) =>
        Step(actor, "Set gaze parts", () => runtime.SetParts(actor, parts));
    public GazeResult SetTarget(ActorId actor, ActorId target) =>
        Step(actor, "Set gaze target", () => runtime.SetTarget(actor, target));
    public GazeResult SetPartLock(ActorId actor, GazeTargetType part, bool locked) =>
        Step(actor, locked ? "Lock gaze part" : "Unlock gaze part",
            () => runtime.SetPartLock(actor, part, locked));
    public GazeResult SnapPartToCamera(ActorId actor, GazeTargetType part) =>
        Step(actor, "Snap gaze to camera", () => runtime.SnapPartToCamera(actor, part));
    public GazeResult Reset(ActorId actor) =>
        Step(actor, "Reset gaze", () => runtime.Reset(actor));

    public GazeResult SetGazePosition(ActorId actor, Vector3 position) =>
        Adjust(actor, GazeTargetType.None, position);
    public GazeResult SetPartPosition(ActorId actor, GazeTargetType part, Vector3 position) =>
        Adjust(actor, part, position);

    private GazeResult Adjust(ActorId actor, GazeTargetType part, Vector3 position)
    {
        if (Read(actor) is not { Settings.Mode: GazeTargetMode.Position })
            return GazeResult.Refused("A live Point-mode gaze target is required.");
        var result = journal.Adjust((actor, part), "Move gaze point",
            () => Read(actor)!.Settings.PartPosition(part),
            next => Written(part == GazeTargetType.None
                ? runtime.SetGazePosition(actor, next)
                : runtime.SetPartPosition(actor, part, next)),
            position, () => Read(actor) is not null);
        return new(result.Success, result.Detail);
    }
}
