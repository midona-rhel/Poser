using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Transforms;

/// <summary>
/// The state of one actor a step was recorded under. A step whose key no
/// longer matches the actor's current key is INVALID: its delta was
/// recorded on a body that is gone (a redraw, a new skeleton, a different
/// timeline, a disruptive verb), so undo restores the actor's snapshot
/// instead of applying the delta.
/// </summary>
public readonly record struct ActorStateKey(
    Guid Lineage,
    ActorId Actor,
    IReadOnlyList<SkeletonId> Slots,
    string Animation,
    ulong Disruption)
{
    public bool Matches(ActorStateKey current) =>
        Actor == current.Actor
        && Slots.SequenceEqual(current.Slots)
        && string.Equals(Animation, current.Animation, StringComparison.Ordinal)
        && Disruption == current.Disruption;
}

/// <summary>One armed IK chain as it was when the snapshot was taken.</summary>
public readonly record struct IkChainSnapshot(BoneId Endpoint, IkChainConfig Config);

/// <summary>
/// An actor's whole pose at one moment: the pose file (opaque here; the
/// runtime port that captured it reads it back) and the armed IK chains.
/// </summary>
public sealed record ActorSnapshot(
    Guid Lineage,
    object Pose,
    IReadOnlyList<IkChainSnapshot> IkChains);

/// <summary>
/// What a step carries beside its delta: the keys it was recorded under,
/// the snapshots that stand in for the delta when a key moved, and the
/// file the redo depends on, if any.
/// </summary>
public sealed record StepContext(
    IReadOnlyList<ActorStateKey> Keys,
    IReadOnlyList<ActorSnapshot> Before,
    IReadOnlyList<ActorSnapshot> After,
    string? Asset = null);

/// <summary>
/// The third entry shape beside the transform patch and the lifecycle
/// patch: a step with its own inverse. It runs the way a lifecycle patch
/// does; the context decides whether it runs at all.
/// </summary>
public sealed record JournalStep(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : HistoryEntry(Description)
{
    /// <summary>The value before and after, when the step is a value
    /// change — read by the action recorder, never by undo.</summary>
    public object? BeforeValue { get; init; }
    public object? AfterValue { get; init; }
}

/// <summary>The current key of an actor, by lineage. Null when the actor
/// is gone.</summary>
public interface IActorStateKeySource
{
    ActorStateKey? Current(Guid lineage);
}

/// <summary>Captures and restores an actor's whole pose. A restore is an
/// import and completes later; the callback says whether it landed.</summary>
public interface IPoseSnapshotPort
{
    ActorSnapshot? Capture(Guid lineage);

    /// <summary>Starts the restore. False when it could not start; the
    /// callback then never fires.</summary>
    bool Restore(ActorSnapshot snapshot, Action<bool> finished);
}

/// <summary>
/// One counter per actor lineage, bumped by every verb that breaks
/// animation state (a redraw, a character file, an appearance apply that
/// redraws). Part of the actor's key, so every step recorded before the
/// bump is invalid after it.
/// </summary>
public sealed class ActorDisruptionEpochs
{
    private readonly Dictionary<Guid, ulong> _epochs = new();

    public ulong Read(Guid lineage) =>
        _epochs.TryGetValue(lineage, out var epoch) ? epoch : 0;

    public void Bump(Guid lineage) => _epochs[lineage] = Read(lineage) + 1;
}

/// <summary>
/// How a producer records a step's context: open a scope on the actors it
/// is about to touch (keys and Before snapshots are taken then), complete
/// it once the step has landed (After snapshots are taken then).
/// </summary>
public sealed class JournalContexts
{
    private readonly IActorStateKeySource _keys;
    private readonly Lazy<IPoseSnapshotPort> _snapshots;

    public JournalContexts(IActorStateKeySource keys, Lazy<IPoseSnapshotPort> snapshots)
    {
        _keys = keys;
        _snapshots = snapshots;
    }

    /// <summary>
    /// Whether steps carry keys and snapshots at all. Off, every scope is
    /// empty: no key is read, no pose is captured, and every step undoes by
    /// its delta — the same rule Brio applies. Disconnected 2026-09-02 on
    /// Midona's call; the machinery stays for the day it is wanted.
    /// </summary>
    public bool StateKeys { get; set; }

    public StepScope BeginActorStep(IEnumerable<Guid> lineages)
    {
        var keys = new List<ActorStateKey>();
        var before = new List<ActorSnapshot>();
        if (!StateKeys)
            return new StepScope(this, keys, before);
        foreach (var lineage in lineages.Distinct())
        {
            if (lineage == Guid.Empty)
                continue;
            if (_keys.Current(lineage) is not { } key)
                continue;
            if (_snapshots.Value.Capture(lineage) is not { } snapshot)
                continue;
            keys.Add(key);
            before.Add(snapshot);
        }
        return new StepScope(this, keys, before);
    }

    private IReadOnlyList<ActorSnapshot> CaptureAfter(IReadOnlyList<ActorStateKey> keys)
    {
        var after = new List<ActorSnapshot>(keys.Count);
        foreach (var key in keys)
            if (_snapshots.Value.Capture(key.Lineage) is { } snapshot)
                after.Add(snapshot);
        return after;
    }

    public sealed class StepScope
    {
        private readonly JournalContexts _owner;
        private readonly IReadOnlyList<ActorStateKey> _keys;
        private readonly IReadOnlyList<ActorSnapshot> _before;

        internal StepScope(
            JournalContexts owner,
            IReadOnlyList<ActorStateKey> keys,
            IReadOnlyList<ActorSnapshot> before)
        {
            _owner = owner;
            _keys = keys;
            _before = before;
        }

        /// <summary>The context for the landed step. After snapshots are
        /// taken now, so call it once the mutation is complete.</summary>
        public StepContext Complete(string? asset = null) =>
            new(_keys, _before, _owner.CaptureAfter(_keys), asset);
    }
}
