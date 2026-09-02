using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Game.Journal;

/// <summary>
/// The verbs that break animation state — a redraw, a character file, an
/// appearance apply that redraws — as journal steps. Each bumps the
/// actor's disruption epoch, so every step recorded before it is invalid
/// after it; its own undo runs the inverse verb and then restores the
/// actor's snapshot from before.
/// </summary>
public sealed class DisruptiveSteps
{
    private readonly TransformHistory _history;
    private readonly IActorStateKeySource _keys;
    private readonly Lazy<IPoseSnapshotPort> _snapshots;
    private readonly ActorDisruptionEpochs _epochs;

    public DisruptiveSteps(
        TransformHistory history,
        IActorStateKeySource keys,
        Lazy<IPoseSnapshotPort> snapshots,
        ActorDisruptionEpochs epochs)
    {
        _history = history;
        _keys = keys;
        _snapshots = snapshots;
        _epochs = epochs;
    }

    /// <summary>
    /// Runs the verb as one step. <paramref name="inverse"/> is what undoes
    /// the verb itself (a reset, the previous assignment); null when the
    /// snapshot is the whole way back (a redraw). <paramref name="asset"/>
    /// is the file the redo depends on.
    /// </summary>
    public IntegrationResult Run(
        ActorId actor,
        string description,
        Func<IntegrationResult> verb,
        Func<IntegrationResult>? inverse = null,
        string? asset = null)
    {
        var lineage = actor.LogicalId;
        var before = _snapshots.Value.Capture(lineage);
        var result = verb();
        if (!result.Success)
            return result;
        _epochs.Bump(lineage);
        // The keys are read AFTER the bump: the step is current until the
        // next disruption, and its undo runs the inverse below.
        var keys = _keys.Current(lineage) is { } key ? new[] { key } : Array.Empty<ActorStateKey>();
        var after = _snapshots.Value.Capture(lineage);
        _history.Append(new JournalStep(
            description,
            () => Back(inverse, before),
            () => Again(verb, after))
        {
            Context = new StepContext(
                keys,
                before is null ? Array.Empty<ActorSnapshot>() : new[] { before },
                after is null ? Array.Empty<ActorSnapshot>() : new[] { after },
                asset),
        });
        return result;
    }

    private bool Back(Func<IntegrationResult>? inverse, ActorSnapshot? before)
    {
        if (inverse is { } undo && !undo().Success)
            return false;
        return before is null || _snapshots.Value.Restore(before, _ => { });
    }

    private bool Again(Func<IntegrationResult> verb, ActorSnapshot? after)
    {
        if (!verb().Success)
            return false;
        return after is null || _snapshots.Value.Restore(after, _ => { });
    }
}
