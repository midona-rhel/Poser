using System.Numerics;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>
/// Gaze as journal steps. Mode, parts, locks, snaps and the reset record
/// the whole gaze state before and after and put it back as one; a point
/// drag is one step on release. The entity target is not restored by an
/// undo: it lives in the game's object table, not in the journal.
/// </summary>
public sealed class GazeSession
{
    private readonly ValueJournal _journal;
    private readonly IGazeService _gaze;
    private readonly IEntityBindings _bindings;

    public GazeSession(ValueJournal journal, IGazeService gaze, IEntityBindings bindings)
    {
        _journal = journal;
        _gaze = gaze;
        _bindings = bindings;
    }

    public void Seal() => _journal.Seal();

    private bool Alive(IActor actor) =>
        _bindings.GetActorId(actor) is { } id && _bindings.Resolve(id).Success;

    private readonly record struct Snapshot(
        GazeTargetMode Mode,
        GazeTargetType Parts,
        Vector3 Position,
        Vector3 Eyes,
        Vector3 Head,
        Vector3 Body,
        bool EyesLocked,
        bool HeadLocked,
        bool BodyLocked);

    private Snapshot Take(IActor actor)
    {
        var state = _gaze.GetGazeState(actor);
        return new Snapshot(
            state.Mode, state.TargetType, state.Position,
            state.EyesPosition, state.HeadPosition, state.BodyPosition,
            _gaze.IsPartLocked(actor, GazeTargetType.Eyes),
            _gaze.IsPartLocked(actor, GazeTargetType.Head),
            _gaze.IsPartLocked(actor, GazeTargetType.Body));
    }

    private void Put(IActor actor, Snapshot s)
    {
        _gaze.SetGazeMode(actor, s.Mode);
        if (s.Mode == GazeTargetMode.None)
            return;
        _gaze.SetGazeParts(actor, s.Parts);
        _gaze.SetGazePosition(actor, s.Position);
        _gaze.SetPartPosition(actor, GazeTargetType.Eyes, s.Eyes);
        _gaze.SetPartPosition(actor, GazeTargetType.Head, s.Head);
        _gaze.SetPartPosition(actor, GazeTargetType.Body, s.Body);
        _gaze.SetPartLock(actor, GazeTargetType.Eyes, s.EyesLocked);
        _gaze.SetPartLock(actor, GazeTargetType.Head, s.HeadLocked);
        _gaze.SetPartLock(actor, GazeTargetType.Body, s.BodyLocked);
    }

    private GazeResult Step(IActor actor, string description, Func<GazeResult> act)
    {
        var before = Take(actor);
        var result = act();
        if (!result.Success)
            return result;
        _journal.Record(description, before, Take(actor), s => Put(actor, s), () => Alive(actor));
        return result;
    }

    public GazeResult SetMode(IActor actor, GazeTargetMode mode) =>
        Step(actor, "Set gaze mode", () => _gaze.SetGazeMode(actor, mode));

    public GazeResult SetParts(IActor actor, GazeTargetType parts) =>
        Step(actor, "Set gaze parts", () => _gaze.SetGazeParts(actor, parts));

    public GazeResult SetTarget(IActor actor, IActor target) =>
        Step(actor, "Set gaze target", () => _gaze.SetGazeTarget(actor, target));

    public void SetPartLock(IActor actor, GazeTargetType part, bool locked) =>
        Step(actor, locked ? "Lock gaze part" : "Unlock gaze part", () =>
        {
            _gaze.SetPartLock(actor, part, locked);
            return GazeResult.Ok();
        });

    public void SnapPartToCamera(IActor actor, GazeTargetType part) =>
        Step(actor, "Snap gaze to camera", () =>
        {
            _gaze.SnapPartToCamera(actor, part);
            return GazeResult.Ok();
        });

    public void Reset(IActor actor) =>
        Step(actor, "Reset gaze", () =>
        {
            _gaze.ResetGaze(actor);
            return GazeResult.Ok();
        });

    /// <summary>A point drag: consecutive positions fold into one step until
    /// <see cref="Seal"/>.</summary>
    public void SetGazePosition(IActor actor, Vector3 position) =>
        _journal.Set((actor, "GazePosition"), "Move gaze point",
            () => _gaze.GetGazeState(actor).Position,
            x => _gaze.SetGazePosition(actor, x), position, () => Alive(actor));

    public void SetPartPosition(IActor actor, GazeTargetType part, Vector3 position) =>
        _journal.Set((actor, part), "Move gaze point",
            () => part switch
            {
                GazeTargetType.Eyes => _gaze.GetGazeState(actor).EyesPosition,
                GazeTargetType.Head => _gaze.GetGazeState(actor).HeadPosition,
                GazeTargetType.Body => _gaze.GetGazeState(actor).BodyPosition,
                _ => _gaze.GetGazeState(actor).Position,
            },
            x => _gaze.SetPartPosition(actor, part, x), position, () => Alive(actor));
}
